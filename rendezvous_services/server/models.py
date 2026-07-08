from __future__ import annotations

from typing import Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, ValidationError


class MessageBase(BaseModel):
    protocol_version: int = Field(default=1, ge=1)
    type: str

    model_config = ConfigDict(extra="forbid")


class RegisterHostMessage(MessageBase):
    type: Literal["register_host"]
    host_id: str = Field(min_length=1, max_length=128)
    max_clients: int = Field(ge=1, le=5)
    metadata: dict[str, Any] = Field(default_factory=dict)


class HostPunchResultMessage(MessageBase):
    type: Literal["punch_result"]
    success: bool
    client_id: str = Field(min_length=1, max_length=128)
    host_id: str = Field(min_length=1, max_length=128)
    session_id: str = Field(min_length=1, max_length=128)


class HostPortMapResultMessage(MessageBase):
    type: Literal["port_map_result"]
    success: bool
    host_id: str = Field(min_length=1, max_length=128)
    session_id: str = Field(min_length=1, max_length=128)
    public_ip: str | None = Field(default=None, min_length=1, max_length=64)
    public_port: int | None = Field(default=None, ge=1, le=65535)


class RegisterClientMessage(MessageBase):
    type: Literal["register_client"]
    client_id: str = Field(min_length=1, max_length=128)


class ListHostsMessage(MessageBase):
    type: Literal["list_hosts"]


class ConnectRequestMessage(MessageBase):
    type: Literal["connect_request"]
    client_id: str = Field(min_length=1, max_length=128)
    host_id: str = Field(min_length=1, max_length=128)


class ClientPunchResultMessage(MessageBase):
    type: Literal["punch_result"]
    success: bool
    client_id: str = Field(min_length=1, max_length=128)
    host_id: str = Field(min_length=1, max_length=128)
    session_id: str = Field(min_length=1, max_length=128)


class ClientRequestPortMapMessage(MessageBase):
    type: Literal["request_port_map"]
    client_id: str = Field(min_length=1, max_length=128)
    host_id: str = Field(min_length=1, max_length=128)
    session_id: str = Field(min_length=1, max_length=128)


class HostSummary(BaseModel):
    host_id: str = Field(min_length=1, max_length=128)
    metadata: dict[str, Any] = Field(default_factory=dict)
    current_clients: int = Field(ge=0)
    max_clients: int = Field(ge=1, le=5)

    model_config = ConfigDict(extra="forbid")


class HostListMessage(MessageBase):
    type: Literal["host_list"]
    hosts: list[HostSummary]


class IncomingClientMessage(MessageBase):
    type: Literal["incoming_client"]
    client_id: str = Field(min_length=1, max_length=128)
    client_public_ip: str = Field(min_length=1, max_length=64)
    client_public_port: int = Field(ge=1, le=65535)
    session_id: str = Field(min_length=1, max_length=128)


class HostEndpointMessage(MessageBase):
    type: Literal["host_endpoint"]
    host_id: str = Field(min_length=1, max_length=128)
    host_public_ip: str = Field(min_length=1, max_length=64)
    host_public_port: int = Field(ge=1, le=65535)
    session_id: str = Field(min_length=1, max_length=128)


class StartPunchMessage(MessageBase):
    type: Literal["start_punch"]
    session_id: str = Field(min_length=1, max_length=128)


class UseRelayMessage(MessageBase):
    type: Literal["use_relay"]
    relay_host: str = Field(min_length=1, max_length=255)
    relay_port: int = Field(ge=1, le=65535)
    session_id: str = Field(min_length=1, max_length=128)


class RequestPortMapMessage(MessageBase):
    type: Literal["request_port_map"]
    session_id: str = Field(min_length=1, max_length=128)
    internal_port: int = Field(ge=1, le=65535)


class ErrorMessage(MessageBase):
    type: Literal["error"]
    code: str = Field(min_length=1, max_length=64)
    message: str = Field(min_length=1, max_length=1024)
    session_id: str | None = Field(default=None, min_length=1, max_length=128)


HostInboundMessage = Union[RegisterHostMessage, HostPunchResultMessage, HostPortMapResultMessage]
ClientInboundMessage = Union[
    RegisterClientMessage,
    ListHostsMessage,
    ConnectRequestMessage,
    ClientPunchResultMessage,
    ClientRequestPortMapMessage,
]
ServerOutboundMessage = Union[
    HostListMessage,
    IncomingClientMessage,
    HostEndpointMessage,
    StartPunchMessage,
    UseRelayMessage,
    RequestPortMapMessage,
    ErrorMessage,
]


_HOST_INBOUND_BY_TYPE = {
    "register_host": RegisterHostMessage,
    "punch_result": HostPunchResultMessage,
    "port_map_result": HostPortMapResultMessage,
}

_CLIENT_INBOUND_BY_TYPE = {
    "register_client": RegisterClientMessage,
    "list_hosts": ListHostsMessage,
    "connect_request": ConnectRequestMessage,
    "punch_result": ClientPunchResultMessage,
    "request_port_map": ClientRequestPortMapMessage,
}

_SERVER_OUTBOUND_BY_TYPE = {
    "host_list": HostListMessage,
    "incoming_client": IncomingClientMessage,
    "host_endpoint": HostEndpointMessage,
    "start_punch": StartPunchMessage,
    "use_relay": UseRelayMessage,
    "request_port_map": RequestPortMapMessage,
    "error": ErrorMessage,
}


def _validate_with(model: type[BaseModel], payload: dict[str, Any]) -> BaseModel:
    if hasattr(model, "model_validate"):
        return model.model_validate(payload)
    return model.parse_obj(payload)


def validate_host_inbound(payload: dict[str, Any]) -> HostInboundMessage:
    msg_type = payload.get("type")
    model = _HOST_INBOUND_BY_TYPE.get(msg_type)
    if model is None:
        raise ValueError(f"unsupported host message type: {msg_type}")
    return _validate_with(model, payload)


def validate_client_inbound(payload: dict[str, Any]) -> ClientInboundMessage:
    msg_type = payload.get("type")
    model = _CLIENT_INBOUND_BY_TYPE.get(msg_type)
    if model is None:
        raise ValueError(f"unsupported client message type: {msg_type}")
    return _validate_with(model, payload)


def validate_server_outbound(payload: dict[str, Any]) -> ServerOutboundMessage:
    msg_type = payload.get("type")
    model = _SERVER_OUTBOUND_BY_TYPE.get(msg_type)
    if model is None:
        raise ValueError(f"unsupported server message type: {msg_type}")
    return _validate_with(model, payload)


def is_validation_error(exc: Exception) -> bool:
    return isinstance(exc, ValidationError)
