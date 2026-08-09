from __future__ import annotations

import os
import socket
import struct
from contextlib import suppress
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any


@dataclass
class PortMapTarget:
    name: str
    port: int
    enabled: bool = True


@dataclass
class PortMapResult:
    name: str
    port: int
    protocol: str = ""
    success: bool = False
    message: str = ""


@dataclass
class PortMapSnapshot:
    enabled: bool
    attempted: bool = False
    attempted_at_utc: str = ""
    gateway_ip: str = ""
    diagnostics: dict[str, Any] = field(default_factory=dict)
    ports: list[PortMapResult] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        successful = sum(1 for p in self.ports if p.success)
        failed = sum(1 for p in self.ports if not p.success)
        return {
            "enabled": self.enabled,
            "attempted": self.attempted,
            "attempted_at_utc": self.attempted_at_utc,
            "gateway_ip": self.gateway_ip,
            "diagnostics": self.diagnostics,
            "summary": {
                "successful": successful,
                "failed": failed,
            },
            "ports": [asdict(p) for p in self.ports],
        }


class RendezvousPortMapper:
    def __init__(
        self,
        enabled: bool,
        mappings: list[tuple[str, int] | tuple[str, int, bool]],
        known_host_ips: list[str] | None = None,
        upnp_internal_ip: str = "",
        natpmp_gateway_ip: str = "",
        lifetime_seconds: int = 3600,
    ) -> None:
        self._enabled = enabled
        self._mappings: list[PortMapTarget] = []
        for mapping in mappings:
            if len(mapping) == 2:
                name, port = mapping
                enabled_flag = True
            else:
                name, port, enabled_flag = mapping

            if 1 <= int(port) <= 65535:
                self._mappings.append(PortMapTarget(name=name, port=port, enabled=bool(enabled_flag)))
        self._lifetime_seconds = max(60, int(lifetime_seconds))
        self._snapshot = PortMapSnapshot(enabled=enabled)
        self._upnp = None
        self._natpmp_gateway = ""
        self._upnp_added_ports: set[int] = set()
        self._natpmp_added_ports: set[int] = set()
        self._known_host_ips = {ip.strip() for ip in (known_host_ips or []) if ip and ip.strip()}
        self._upnp_internal_ip_override = upnp_internal_ip.strip()
        self._natpmp_gateway_override = natpmp_gateway_ip.strip()
        self._docker_host_ips_cache: set[str] = set()

    @property
    def snapshot(self) -> PortMapSnapshot:
        return self._snapshot

    def run_mapping(self) -> PortMapSnapshot:
        self._snapshot = PortMapSnapshot(enabled=self._enabled)

        if not self._enabled:
            return self._snapshot

        self._snapshot.attempted = True
        self._snapshot.attempted_at_utc = datetime.now(timezone.utc).isoformat()

        upnp = self._init_upnp()
        self._natpmp_gateway = self._natpmp_gateway_override or self._find_default_gateway_ipv4()
        self._snapshot.gateway_ip = self._natpmp_gateway
        self._docker_host_ips_cache = self._discover_docker_host_ips()

        upnp_lan_ip = ""
        if upnp is not None:
            upnp_lan_ip = str(getattr(upnp, "lanaddr", ""))

        upnp_internal_target_ip = self._upnp_internal_ip_override or upnp_lan_ip
        natpmp_gateway_source = "override" if self._natpmp_gateway_override else "auto"

        self._snapshot.diagnostics = {
            "upnp_discovered": upnp is not None,
            "upnp_lan_ip": upnp_lan_ip,
            "upnp_internal_target_ip": upnp_internal_target_ip,
            "natpmp_gateway_ip": self._natpmp_gateway,
            "natpmp_gateway_source": natpmp_gateway_source,
            "known_host_ips": sorted(self._known_host_ips),
            "docker_host_ips": sorted(self._docker_host_ips_cache),
        }

        for mapping in self._mappings:
            result = PortMapResult(name=mapping.name, port=mapping.port)

            if not mapping.enabled:
                result.success = False
                result.protocol = "none"
                result.message = "Not Enabled"
                self._snapshot.ports.append(result)
                continue

            already_mapped, _ = self._is_already_mapped_upnp(upnp, mapping.port)
            if already_mapped:
                result.success = True
                result.protocol = "upnp"
                result.message = "Success - port mapped"
                self._snapshot.ports.append(result)
                continue

            upnp_ok, _ = self._try_map_upnp(upnp, mapping.port)
            if upnp_ok:
                result.success = True
                result.protocol = "upnp"
                result.message = "Success - UPnP"
                self._snapshot.ports.append(result)
                continue

            nat_ok, _ = self._try_map_natpmp(self._natpmp_gateway, mapping.port)
            if nat_ok:
                result.success = True
                result.protocol = "nat-pmp"
                result.message = "Success - NAT-PMP"
            else:
                result.success = False
                if self._natpmp_gateway:
                    result.protocol = "nat-pmp"
                    result.message = "Failed - NAT-PMP"
                else:
                    result.protocol = "upnp"
                    result.message = "Failed - UPnP"

            self._snapshot.ports.append(result)

        return self._snapshot

    def clear_mappings(self) -> None:
        for port in list(self._upnp_added_ports):
            self._clear_upnp(port)

        for port in list(self._natpmp_added_ports):
            self._clear_natpmp(self._natpmp_gateway, port)

    def _init_upnp(self):
        try:
            import miniupnpc  # type: ignore

            upnp = miniupnpc.UPnP()
            upnp.discoverdelay = 2000
            discovered = upnp.discover()
            if discovered <= 0:
                return None
            upnp.selectigd()
            self._upnp = upnp
            return upnp
        except Exception:
            return None

    def _try_map_upnp(self, upnp, port: int) -> tuple[bool, str]:
        if upnp is None:
            return False, "no UPnP IGD discovered"

        try:
            target_internal_ip = self._upnp_internal_ip_override or str(upnp.lanaddr)
            ok = upnp.addportmapping(port, "TCP", target_internal_ip, port, "NetKeyer rendezvous", "", self._lifetime_seconds)
            if ok:
                self._upnp_added_ports.add(port)
                return True, "mapped"

            return False, "addportmapping returned false"
        except Exception as ex:
            return False, str(ex)

    def _is_already_mapped_upnp(self, upnp, port: int) -> tuple[bool, str]:
        if upnp is None:
            return False, "no UPnP IGD discovered"

        try:
            mapping = upnp.getspecificportmapping(port, "TCP")
            if not mapping:
                return False, "not mapped"

            # miniupnpc may return a tuple/list with lan address as first item.
            # In containerized deployments, the router may be mapped to the Docker host LAN IP
            # instead of the container IP, so allow explicit host IP hints as valid matches.
            mapping_lan_addr = str(mapping[0]) if isinstance(mapping, (tuple, list)) and mapping else ""
            valid_lan_addrs = {
                str(upnp.lanaddr),
                *self._known_host_ips,
                *self._docker_host_ips_cache,
            }

            if self._upnp_internal_ip_override:
                valid_lan_addrs.add(self._upnp_internal_ip_override)

            # The default container gateway commonly represents the Docker host side
            # of the bridge network, which is often the actual UPnP mapping target.
            if self._natpmp_gateway:
                valid_lan_addrs.add(self._natpmp_gateway)

            if mapping_lan_addr in valid_lan_addrs:
                return True, "mapped to this host"

            return False, "mapped to different host"
        except Exception as ex:
            return False, str(ex)

    def _discover_docker_host_ips(self) -> set[str]:
        host_ips: set[str] = set()
        for hostname in ("host.docker.internal", "gateway.docker.internal"):
            try:
                ip = socket.gethostbyname(hostname)
                if ip:
                    host_ips.add(ip)
            except Exception:
                continue
        return host_ips

    def _clear_upnp(self, port: int) -> None:
        if self._upnp is None:
            return

        try:
            self._upnp.deleteportmapping(port, "TCP")
        except Exception:
            pass
        finally:
            self._upnp_added_ports.discard(port)

    def _try_map_natpmp(self, gateway_ip: str, port: int) -> tuple[bool, str]:
        if not gateway_ip:
            return False, "default gateway not found"

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.settimeout(2.0)

            # NAT-PMP TCP mapping request:
            # version(0), opcode(2), reserved(0), internal_port, requested_external_port, lifetime
            req = struct.pack("!BBHHHI", 0, 2, 0, port, port, self._lifetime_seconds)
            sock.sendto(req, (gateway_ip, 5351))
            data, _ = sock.recvfrom(32)

            if len(data) < 16:
                return False, "short response"

            version, opcode, result_code, _epoch, _internal_port, external_port, lifetime = struct.unpack("!BBH I H H I", data[:16])
            if version != 0:
                return False, f"unexpected version {version}"
            if opcode != 130:
                return False, f"unexpected opcode {opcode}"
            if result_code != 0:
                return False, f"router error {result_code}"
            if external_port <= 0 or lifetime <= 0:
                return False, "invalid mapping response"

            self._natpmp_added_ports.add(port)
            return True, "mapped"
        except Exception as ex:
            return False, str(ex)
        finally:
            sock.close()

    def _clear_natpmp(self, gateway_ip: str, port: int) -> None:
        if not gateway_ip:
            self._natpmp_added_ports.discard(port)
            return

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.settimeout(1.0)
            req = struct.pack("!BBHHHI", 0, 2, 0, port, port, 0)
            sock.sendto(req, (gateway_ip, 5351))
            with suppress(TimeoutError):
                sock.recvfrom(32)
        except Exception:
            pass
        finally:
            sock.close()
            self._natpmp_added_ports.discard(port)

    def _find_default_gateway_ipv4(self) -> str:
        # Linux default route lookup from /proc/net/route.
        # In containerized Linux deployments this is the common and reliable path.
        route_file = "/proc/net/route"
        if not os.path.exists(route_file):
            return ""

        try:
            with open(route_file, "r", encoding="utf-8") as f:
                lines = f.readlines()[1:]

            for line in lines:
                fields = line.strip().split()
                if len(fields) < 3:
                    continue
                destination = fields[1]
                gateway_hex = fields[2]
                if destination != "00000000":
                    continue

                gateway_raw = struct.pack("<L", int(gateway_hex, 16))
                return socket.inet_ntoa(gateway_raw)
        except Exception:
            return ""

        return ""
