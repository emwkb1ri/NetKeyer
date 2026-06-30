import unittest

from pydantic import ValidationError

from server.models import (
    validate_client_inbound,
    validate_host_inbound,
    validate_server_outbound,
)


class TestHostInboundValidation(unittest.TestCase):
    def test_register_host_valid(self) -> None:
        msg = validate_host_inbound(
            {
                "type": "register_host",
                "protocol_version": 1,
                "host_id": "host-alpha",
                "max_clients": 5,
                "metadata": {"name": "Alpha", "band": "20m"},
            }
        )
        self.assertEqual(msg.type, "register_host")
        self.assertEqual(msg.max_clients, 5)

    def test_register_host_rejects_out_of_range_max_clients(self) -> None:
        with self.assertRaises(ValidationError):
            validate_host_inbound(
                {
                    "type": "register_host",
                    "host_id": "host-alpha",
                    "max_clients": 6,
                    "metadata": {},
                }
            )

    def test_host_unknown_message_type(self) -> None:
        with self.assertRaises(ValueError):
            validate_host_inbound({"type": "list_hosts"})


class TestClientInboundValidation(unittest.TestCase):
    def test_register_client_valid(self) -> None:
        msg = validate_client_inbound(
            {
                "type": "register_client",
                "client_id": "client-1",
            }
        )
        self.assertEqual(msg.type, "register_client")

    def test_connect_request_requires_ids(self) -> None:
        with self.assertRaises(ValidationError):
            validate_client_inbound(
                {
                    "type": "connect_request",
                    "client_id": "client-1",
                }
            )

    def test_punch_result_requires_session_id(self) -> None:
        with self.assertRaises(ValidationError):
            validate_client_inbound(
                {
                    "type": "punch_result",
                    "success": False,
                    "client_id": "client-1",
                    "host_id": "host-1",
                }
            )


class TestServerOutboundValidation(unittest.TestCase):
    def test_host_list_valid(self) -> None:
        msg = validate_server_outbound(
            {
                "type": "host_list",
                "hosts": [
                    {
                        "host_id": "host-1",
                        "metadata": {"name": "Alpha"},
                        "current_clients": 0,
                        "max_clients": 5,
                    }
                ],
            }
        )
        self.assertEqual(msg.type, "host_list")
        self.assertEqual(len(msg.hosts), 1)

    def test_use_relay_valid(self) -> None:
        msg = validate_server_outbound(
            {
                "type": "use_relay",
                "relay_host": "relay.example.net",
                "relay_port": 6000,
                "session_id": "sess-123",
            }
        )
        self.assertEqual(msg.type, "use_relay")
        self.assertEqual(msg.relay_port, 6000)

    def test_server_message_rejects_extra_fields(self) -> None:
        with self.assertRaises(ValidationError):
            validate_server_outbound(
                {
                    "type": "start_punch",
                    "session_id": "sess-123",
                    "unexpected": "x",
                }
            )


if __name__ == "__main__":
    unittest.main()
