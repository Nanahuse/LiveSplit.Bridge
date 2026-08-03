from __future__ import annotations

import argparse
import json
import os
import sys

import zmq
from google.protobuf.json_format import MessageToDict

from livesplit.bridge.v1 import common_pb2

from .client import (
    GAME_TIME_OPERATIONS,
    TIMER_OPERATIONS,
    BridgeClient,
    BridgeClientError,
)

DEFAULT_RPC_ENDPOINT = "tcp://127.0.0.1:54000"
DEFAULT_EVENT_ENDPOINT = "tcp://127.0.0.1:54001"
TICKS_PER_SECOND = 10_000_000


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description="Debug LiveSplit.Bridge over ZeroMQ")
    result.add_argument(
        "--rpc-endpoint",
        default=os.getenv("LIVESPLIT_BRIDGE_RPC_ENDPOINT", DEFAULT_RPC_ENDPOINT),
    )
    result.add_argument(
        "--event-endpoint",
        default=os.getenv("LIVESPLIT_BRIDGE_EVENT_ENDPOINT", DEFAULT_EVENT_ENDPOINT),
    )
    result.add_argument(
        "--timeout", type=float, default=3.0, help="RPC timeout in seconds (default: 3)"
    )
    result.add_argument(
        "--json", action="store_true", help="Print protobuf messages as JSON"
    )
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("attach", help="Attach and show session plus initial snapshot")
    commands.add_parser("snapshot", help="Get the current timer snapshot")

    timer = commands.add_parser("timer", help="Execute a timer operation")
    timer.add_argument("operation", choices=TIMER_OPERATIONS)

    game_time = commands.add_parser("game-time", help="Execute a game-time operation")
    game_time.add_argument(
        "operation", choices=["initialize", "set", "pause", "resume"]
    )
    game_time.add_argument(
        "seconds", type=float, nargs="?", help="Game time in seconds (required by set)"
    )

    events = commands.add_parser("events", help="Continuously monitor bridge events")
    events.add_argument(
        "--count", type=int, help="Exit after receiving this many events"
    )
    return result


def message_dict(message: object) -> dict[str, object]:
    return MessageToDict(message, preserving_proto_field_name=True)


def format_ticks(ticks: int) -> str:
    negative = ticks < 0
    value = abs(ticks)
    hours, remainder = divmod(value, 3600 * TICKS_PER_SECOND)
    minutes, remainder = divmod(remainder, 60 * TICKS_PER_SECOND)
    seconds = remainder / TICKS_PER_SECOND
    prefix = "-" if negative else ""
    return f"{prefix}{hours}:{minutes:02d}:{seconds:06.3f}"


def snapshot_lines(snapshot: common_pb2.TimerSnapshot) -> list[str]:
    phase = common_pb2.TimerPhase.Name(snapshot.phase)
    real_time = (
        format_ticks(snapshot.real_time_ticks)
        if snapshot.HasField("real_time_ticks")
        else "-"
    )
    game_time = (
        format_ticks(snapshot.game_time_ticks)
        if snapshot.HasField("game_time_ticks")
        else "-"
    )
    return [
        f"session={snapshot.session_id} sequence={snapshot.event_sequence} "
        f"revision={snapshot.state_revision}",
        f"phase={phase} split={snapshot.split_index}/{snapshot.split_count} "
        f"paused={snapshot.is_paused}",
        f"real_time={real_time} game_time={game_time} "
        f"game_time_initialized={snapshot.is_game_time_initialized}",
    ]


def print_message(message: object, as_json: bool) -> None:
    if as_json:
        print(json.dumps(message_dict(message), ensure_ascii=False, indent=2))
        return
    if isinstance(message, common_pb2.TimerSnapshot):
        print("\n".join(snapshot_lines(message)))
    else:
        print(message)


def run_events(endpoint: str, as_json: bool, count: int | None) -> int:
    context = zmq.Context()
    socket = context.socket(zmq.SUB)
    socket.setsockopt(zmq.LINGER, 0)
    socket.setsockopt(zmq.SUBSCRIBE, b"")
    socket.connect(endpoint)
    received = 0
    print(f"Monitoring {endpoint} (Ctrl+C to stop)", file=sys.stderr)
    try:
        while count is None or received < count:
            event = common_pb2.BridgeEvent.FromString(socket.recv())
            if as_json:
                print_message(event, True)
            else:
                event_type = common_pb2.BridgeEventType.Name(event.type)
                print(f"[{event.event_sequence}] {event_type}: {event.description}")
                if event.HasField("snapshot"):
                    print("  " + "\n  ".join(snapshot_lines(event.snapshot)))
            received += 1
    except KeyboardInterrupt:
        return 0
    finally:
        socket.close()
        context.term()
    return 0


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    if args.timeout <= 0:
        parser().error("--timeout must be greater than zero")
    if args.command == "events":
        return run_events(args.event_endpoint, args.json, args.count)

    try:
        with BridgeClient(args.rpc_endpoint, round(args.timeout * 1000)) as client:
            if args.command == "attach":
                response = client.attach()
                print_message(
                    response if args.json else response.attach.snapshot, args.json
                )
            elif args.command == "snapshot":
                response = client.snapshot()
                print_message(
                    response if args.json else response.get_snapshot.snapshot, args.json
                )
            elif args.command == "timer":
                response = client.timer(TIMER_OPERATIONS[args.operation])
                print_message(response if args.json else response.operation, args.json)
                if not response.operation.success:
                    return 2
            elif args.command == "game-time":
                if args.operation == "set":
                    if args.seconds is None:
                        parser().error("game-time set requires seconds")
                    ticks = round(args.seconds * TICKS_PER_SECOND)
                    response = client.game_time("SET", ticks)
                else:
                    if args.seconds is not None:
                        parser().error(
                            f"game-time {args.operation} does not accept seconds"
                        )
                    response = client.game_time(GAME_TIME_OPERATIONS[args.operation])
                print_message(response if args.json else response.operation, args.json)
                if not response.operation.success:
                    return 2
    except (BridgeClientError, zmq.ZMQError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
