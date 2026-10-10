from __future__ import annotations

import argparse
import json
import os
import sys

import websocket
from google.protobuf.json_format import MessageToDict

from livesplit.bridge.v3 import common_pb2, run_pb2

from .client import (
    GAME_TIME_OPERATIONS,
    TIMER_OPERATIONS,
    BridgeClient,
    BridgeClientError,
)

DEFAULT_PORT = 54000
RPC_PATH = "/bridge/v3/rpc"
EVENTS_PATH = "/bridge/v3/events"
TICKS_PER_SECOND = 10_000_000


def rpc_url(port: int) -> str:
    return f"ws://127.0.0.1:{port}{RPC_PATH}"


def events_url(port: int) -> str:
    return f"ws://127.0.0.1:{port}{EVENTS_PATH}"


def default_port() -> int:
    return int(os.getenv("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", str(DEFAULT_PORT)))


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(
        description="Debug LiveSplit.Bridge over WebSocket"
    )
    result.add_argument(
        "--port",
        type=int,
        default=default_port(),
        help="WebSocket port of the bridge (default: 54000)",
    )
    result.add_argument(
        "--timeout", type=float, default=3.0, help="RPC timeout in seconds (default: 3)"
    )
    result.add_argument(
        "--json", action="store_true", help="Print protobuf messages as JSON"
    )
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("timer-state", help="Get the current timer state")
    commands.add_parser("run", help="Get the currently loaded run state")
    commands.add_parser("attempt", help="Get the current attempt state")
    commands.add_parser("context", help="Get the current context state")
    commands.add_parser("completed-count", help="Get the completed attempt count")

    timer = commands.add_parser("timer", help="Execute a timer operation")
    timer.add_argument("operation", choices=TIMER_OPERATIONS)

    game_time = commands.add_parser("game-time", help="Execute a game-time operation")
    game_time.add_argument("operation", choices=[*GAME_TIME_OPERATIONS, "set"])
    game_time.add_argument(
        "seconds", type=float, nargs="?", help="Game time in seconds (required by set)"
    )

    events = commands.add_parser("events", help="Monitor bridge events")
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


def optional_ticks(value: object, field: str) -> str:
    return format_ticks(getattr(value, field)) if value.HasField(field) else "-"


def timer_state_lines(state: common_pb2.TimerState) -> list[str]:
    phase = common_pb2.TimerPhase.Name(state.phase)
    return [
        f"phase={phase} split_index={state.split_index}",
        f"real_time={format_ticks(state.real_time_ticks)} "
        f"game_time={optional_ticks(state, 'game_time_ticks')} "
        f"game_time_initialized={state.is_game_time_initialized} "
        f"game_time_paused={state.is_game_time_paused}",
    ]


def time_value(value: common_pb2.TimeValue) -> str:
    return (
        f"real_time={optional_ticks(value, 'real_time_ticks')} "
        f"game_time={optional_ticks(value, 'game_time_ticks')}"
    )


def run_lines(run: run_pb2.RunState) -> list[str]:
    lines = [
        f"game={run.game_name or '-'} category={run.category_name or '-'}",
        f"comparisons={list(run.comparisons)}",
    ]
    for segment in run.segments:
        lines.append(f"  [{segment.index}] {segment.name}")
        lines.extend(
            f"    {comparison.name}: {time_value(comparison.time)}"
            for comparison in segment.comparisons
        )
    return lines


def attempt_lines(attempt: common_pb2.AttemptState) -> list[str]:
    lines = [f"attempt_count={attempt.attempt_count}"]
    lines.extend(
        f"  [{segment.index}] {time_value(segment.split_time)}"
        for segment in attempt.segments
    )
    return lines


def context_lines(context: common_pb2.ContextState) -> list[str]:
    lines = [
        f"timing_method={common_pb2.TimingMethod.Name(context.current_timing_method)}",
        f"current_comparison={context.current_comparison or '-'}",
    ]
    lines.extend(
        f"  custom_variable {name}={value}"
        for name, value in sorted(context.custom_variables.items())
    )
    return lines


def print_message(message: object, as_json: bool) -> None:
    if as_json:
        print(json.dumps(message_dict(message), ensure_ascii=False, indent=2))
    elif isinstance(message, common_pb2.TimerState):
        print("\n".join(timer_state_lines(message)))
    elif isinstance(message, common_pb2.AttemptState):
        print("\n".join(attempt_lines(message)))
    elif isinstance(message, common_pb2.ContextState):
        print("\n".join(context_lines(message)))
    elif isinstance(message, run_pb2.RunState):
        print("\n".join(run_lines(message)))
    elif isinstance(message, common_pb2.CompletedCount):
        print(f"completed_count={message.completed_count}")
    else:
        print(message)


def run_events(endpoint: str, as_json: bool, count: int | None) -> int:
    try:
        socket = websocket.create_connection(endpoint, timeout=None)
    except (OSError, websocket.WebSocketException) as error:
        raise BridgeClientError(f"Failed to connect to {endpoint}: {error}") from error
    received = 0
    print(f"Monitoring {endpoint} (Ctrl+C to stop)", file=sys.stderr)
    try:
        while count is None or received < count:
            data = socket.recv()
            if isinstance(data, str):
                raise BridgeClientError("Bridge returned a text frame; binary expected")
            event = common_pb2.BridgeEvent.FromString(data)
            if as_json:
                print_message(event, True)
            else:
                event_type = common_pb2.BridgeEventType.Name(event.type)
                print(f"[{event.event_sequence}] {event_type}")
                if event.HasField("timer_state"):
                    print("  " + "\n  ".join(timer_state_lines(event.timer_state)))
            received += 1
    except KeyboardInterrupt:
        return 0
    except websocket.WebSocketException as error:
        raise BridgeClientError(f"Events failed: {error}") from error
    finally:
        socket.close()
    return 0


def main(argv: list[str] | None = None) -> int:
    argument_parser = parser()
    args = argument_parser.parse_args(argv)
    if args.timeout <= 0:
        argument_parser.error("--timeout must be greater than zero")
    if not 1 <= args.port <= 65535:
        argument_parser.error("--port must be between 1 and 65535")
    if args.command == "events" and args.count is not None and args.count < 1:
        argument_parser.error("events --count must be greater than zero")

    try:
        if args.command == "events":
            return run_events(events_url(args.port), args.json, args.count)
        with BridgeClient(rpc_url(args.port), round(args.timeout * 1000)) as client:
            match args.command:
                case "timer-state":
                    response = client.timer_state()
                    print_message(
                        response if args.json else response.get_timer_state.timer_state,
                        args.json,
                    )
                case "run":
                    response = client.run()
                    print_message(
                        response if args.json else response.get_run.run, args.json
                    )
                case "attempt":
                    response = client.attempt()
                    print_message(
                        response if args.json else response.get_attempt.attempt,
                        args.json,
                    )
                case "context":
                    response = client.context()
                    print_message(
                        response
                        if args.json
                        else response.get_context_state.context_state,
                        args.json,
                    )
                case "completed-count":
                    response = client.completed_count()
                    print_message(
                        response
                        if args.json
                        else response.get_completed_count.completed_count,
                        args.json,
                    )
                case "timer":
                    response = client.timer(TIMER_OPERATIONS[args.operation])
                    print_message(
                        response if args.json else response.operation, args.json
                    )
                case "game-time":
                    if args.operation == "set":
                        if args.seconds is None:
                            argument_parser.error("game-time set requires seconds")
                        ticks = round(args.seconds * TICKS_PER_SECOND)
                        response = client.game_time(common_pb2.SET, ticks)
                    else:
                        if args.seconds is not None:
                            argument_parser.error(
                                f"game-time {args.operation} does not accept seconds"
                            )
                        response = client.game_time(
                            GAME_TIME_OPERATIONS[args.operation]
                        )
                    print_message(
                        response if args.json else response.operation, args.json
                    )
    except (BridgeClientError, websocket.WebSocketException, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
