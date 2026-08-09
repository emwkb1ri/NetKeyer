import unittest

from server.port_mapping import RendezvousPortMapper


class FakeMapper(RendezvousPortMapper):
    def __init__(self, enabled: bool, mappings, gateway: str = "192.0.2.1") -> None:
        super().__init__(enabled=enabled, mappings=mappings)
        self.gateway = gateway
        self.upnp_available = True
        self.already_ports: set[int] = set()
        self.upnp_success_ports: set[int] = set()
        self.nat_success_ports: set[int] = set()
        self.upnp_attempts = 0
        self.nat_attempts = 0

    def _init_upnp(self):
        return object() if self.upnp_available else None

    def _find_default_gateway_ipv4(self) -> str:
        return self.gateway

    def _is_already_mapped_upnp(self, upnp, port: int):
        return (port in self.already_ports, "")

    def _try_map_upnp(self, upnp, port: int):
        self.upnp_attempts += 1
        return (port in self.upnp_success_ports, "")

    def _try_map_natpmp(self, gateway_ip: str, port: int):
        self.nat_attempts += 1
        return (port in self.nat_success_ports, "")


class TestPortMappingStatus(unittest.TestCase):
    def test_snapshot_includes_diagnostics_block(self) -> None:
        mapper = FakeMapper(enabled=True, mappings=[("relay", 49921, True)], gateway="172.23.0.1")
        mapper._discover_docker_host_ips = lambda: {"172.23.0.1"}  # type: ignore[method-assign]

        snapshot = mapper.run_mapping().to_dict()

        self.assertIn("diagnostics", snapshot)
        diagnostics = snapshot["diagnostics"]
        self.assertEqual(diagnostics["natpmp_gateway_ip"], "172.23.0.1")
        self.assertEqual(diagnostics["natpmp_gateway_source"], "auto")
        self.assertIn("docker_host_ips", diagnostics)

    def test_not_enabled_for_optional_nginx_port(self) -> None:
        mapper = FakeMapper(
            enabled=True,
            mappings=[
                ("rendezvous_control", 49920, True),
                ("relay", 49921, True),
                ("nginx_relay_proxy", 49922, False),
            ],
        )
        mapper.already_ports.update({49920, 49921})

        snapshot = mapper.run_mapping()
        ports = {p.name: p for p in snapshot.ports}

        self.assertEqual(ports["nginx_relay_proxy"].message, "Not Enabled")
        self.assertFalse(ports["nginx_relay_proxy"].success)

    def test_already_mapped_short_circuits_open_attempts(self) -> None:
        mapper = FakeMapper(enabled=True, mappings=[("relay", 49921, True)])
        mapper.already_ports.add(49921)

        snapshot = mapper.run_mapping()

        self.assertEqual(len(snapshot.ports), 1)
        self.assertEqual(snapshot.ports[0].message, "Success - port mapped")
        self.assertEqual(mapper.upnp_attempts, 0)
        self.assertEqual(mapper.nat_attempts, 0)

    def test_success_messages_for_upnp_and_nat_pmp(self) -> None:
        mapper = FakeMapper(
            enabled=True,
            mappings=[
                ("rendezvous_control", 49920, True),
                ("relay", 49921, True),
            ],
        )
        mapper.upnp_success_ports.add(49920)
        mapper.nat_success_ports.add(49921)

        snapshot = mapper.run_mapping()
        by_port = {p.port: p for p in snapshot.ports}

        self.assertEqual(by_port[49920].message, "Success - UPnP")
        self.assertEqual(by_port[49921].message, "Success - NAT-PMP")

    def test_failed_message_prefers_nat_pmp_when_gateway_present(self) -> None:
        mapper = FakeMapper(enabled=True, mappings=[("relay", 49921, True)], gateway="192.0.2.1")

        snapshot = mapper.run_mapping()

        self.assertEqual(snapshot.ports[0].message, "Failed - NAT-PMP")

    def test_failed_message_is_upnp_without_gateway(self) -> None:
        mapper = FakeMapper(enabled=True, mappings=[("relay", 49921, True)], gateway="")

        snapshot = mapper.run_mapping()

        self.assertEqual(snapshot.ports[0].message, "Failed - UPnP")

    def test_already_mapped_detects_known_host_ip_hint(self) -> None:
        class FakeUPnP:
            def __init__(self) -> None:
                self.lanaddr = "172.23.0.2"

            def getspecificportmapping(self, port: int, protocol: str):
                self.last_port = port
                self.last_protocol = protocol
                return ("192.168.1.50", "49921", "TCP", "manual", "0")

        mapper = RendezvousPortMapper(
            enabled=True,
            mappings=[("relay", 49921, True)],
            known_host_ips=["192.168.1.50"],
        )
        mapper._discover_docker_host_ips = lambda: set()  # type: ignore[method-assign]
        ok, _ = mapper._is_already_mapped_upnp(FakeUPnP(), 49921)

        self.assertTrue(ok)

    def test_already_mapped_rejects_different_host(self) -> None:
        class FakeUPnP:
            def __init__(self) -> None:
                self.lanaddr = "172.23.0.2"

            def getspecificportmapping(self, port: int, protocol: str):
                self.last_port = port
                self.last_protocol = protocol
                return ("192.168.1.77", "49921", "TCP", "manual", "0")

        mapper = RendezvousPortMapper(
            enabled=True,
            mappings=[("relay", 49921, True)],
            known_host_ips=["192.168.1.50"],
        )
        mapper._discover_docker_host_ips = lambda: set()  # type: ignore[method-assign]
        ok, _ = mapper._is_already_mapped_upnp(FakeUPnP(), 49921)

        self.assertFalse(ok)

    def test_already_mapped_detects_gateway_host_ip(self) -> None:
        class FakeUPnP:
            def __init__(self) -> None:
                self.lanaddr = "172.23.0.2"

            def getspecificportmapping(self, port: int, protocol: str):
                self.last_port = port
                self.last_protocol = protocol
                return ("172.23.0.1", "49921", "TCP", "manual", "0")

        mapper = RendezvousPortMapper(
            enabled=True,
            mappings=[("relay", 49921, True)],
        )
        mapper._natpmp_gateway = "172.23.0.1"
        mapper._discover_docker_host_ips = lambda: set()  # type: ignore[method-assign]
        ok, _ = mapper._is_already_mapped_upnp(FakeUPnP(), 49921)

        self.assertTrue(ok)

    def test_already_mapped_rejects_non_gateway_when_gateway_used(self) -> None:
        class FakeUPnP:
            def __init__(self) -> None:
                self.lanaddr = "172.23.0.2"

            def getspecificportmapping(self, port: int, protocol: str):
                self.last_port = port
                self.last_protocol = protocol
                return ("172.23.0.77", "49921", "TCP", "manual", "0")

        mapper = RendezvousPortMapper(
            enabled=True,
            mappings=[("relay", 49921, True)],
        )
        mapper._natpmp_gateway = "172.23.0.1"
        mapper._discover_docker_host_ips = lambda: set()  # type: ignore[method-assign]
        ok, _ = mapper._is_already_mapped_upnp(FakeUPnP(), 49921)

        self.assertFalse(ok)

    def test_upnp_open_uses_internal_ip_override(self) -> None:
        class FakeUPnP:
            def __init__(self) -> None:
                self.lanaddr = "172.23.0.2"
                self.called_with_internal_ip = ""

            def addportmapping(self, port: int, protocol: str, internal_ip: str, internal_port: int, desc: str, remote_host: str, lifetime: int):
                self.called_with_internal_ip = internal_ip
                return True

        mapper = RendezvousPortMapper(
            enabled=True,
            mappings=[("relay", 49921, True)],
            upnp_internal_ip="192.168.1.50",
        )
        fake = FakeUPnP()

        ok, _ = mapper._try_map_upnp(fake, 49921)

        self.assertTrue(ok)
        self.assertEqual(fake.called_with_internal_ip, "192.168.1.50")

    def test_natpmp_gateway_override_used(self) -> None:
        class FakeMapperWithGatewayCapture(FakeMapper):
            def __init__(self, enabled: bool, mappings, gateway: str = "172.23.0.1") -> None:
                super().__init__(enabled=enabled, mappings=mappings, gateway=gateway)
                self.last_nat_gateway = ""

            def _try_map_natpmp(self, gateway_ip: str, port: int):
                self.last_nat_gateway = gateway_ip
                return super()._try_map_natpmp(gateway_ip, port)

        mapper = FakeMapperWithGatewayCapture(enabled=True, mappings=[("relay", 49921, True)], gateway="172.23.0.1")
        mapper.nat_success_ports.add(49921)
        mapper._natpmp_gateway_override = "192.168.1.1"

        mapper.run_mapping()

        self.assertEqual(mapper.last_nat_gateway, "192.168.1.1")

    def test_snapshot_diagnostics_marks_gateway_override(self) -> None:
        mapper = FakeMapper(enabled=True, mappings=[("relay", 49921, True)], gateway="172.23.0.1")
        mapper._natpmp_gateway_override = "192.168.1.1"

        diagnostics = mapper.run_mapping().to_dict()["diagnostics"]

        self.assertEqual(diagnostics["natpmp_gateway_ip"], "192.168.1.1")
        self.assertEqual(diagnostics["natpmp_gateway_source"], "override")


if __name__ == "__main__":
    unittest.main()
