from __future__ import annotations

import argparse
import json
import os
import sys

import websocket
from google.protobuf.json_format import MessageToDict

from livesplit.bridge.v2 import common_pb2, run_pb2

from .client import (
    GAME_TIME_OPERATIONS,
    TIMER_OPERATIONS,
    BridgeClient,
    BridgeClientError,
)

DEFAULT_PORT = 54000
RPC_PATH = "/bridge/v2/rpc"
EVENTS_PATH = "/bridge/v2/events"
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
    commands.add_parser(
        "attach", help="Attach and show session plus initial timer state"
    )
    commands.add_parser("timer-state", help="Get the current timer state")
    commands.add_parser("run", help="Get the currently loaded run state")
    commands.add_parser("attempt", help="Get the current attempt state")
    commands.add_parser("runtime", help="Get the current runtime state")

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


def timer_state_lines(state: common_pb2.TimerState) -> list[str]:
    phase = common_pb2.TimerPhase.Name(state.phase)
    real_time = (
        format_ticks(state.real_time_ticks)
        if state.HasField("real_time_ticks")
        else "-"
    )
    game_time = (
        format_ticks(state.game_time_ticks)
        if state.HasField("game_time_ticks")
        else "-"
    )
    return [
        f"session={state.session_id} revision={state.state_revision} "
        f"run_revision={state.run_revision} attempt_revision={state.attempt_revision} "
        f"runtime_revision={state.runtime_revision}",
        f"phase={phase} split_index={state.split_index}",
        f"real_time={real_time} game_time={game_time} "
        f"game_time_initialized={state.is_game_time_initialized} "
        f"game_time_paused={state.is_game_time_paused}",
    ]


def run_lines(run: run_pb2.RunState) -> list[str]:
    lines = [
        f"session={run.session_id} run_revision={run.run_revision}",
        f"game={run.game_name or '-'} category={run.category_name or '-'} "
        f"comparisons={list(run.comparisons)}",
    ]
    lines.extend(f"  [{segment.index}] {segment.name}" for segment in run.segments)
    return lines


def attempt_lines(attempt: common_pb2.AttemptState) -> list[str]:
    return [
        f"session={attempt.session_id} attempt_revision={attempt.attempt_revision} "
        f"attempt_count={attempt.attempt_count} completed={attempt.completed_count}",
        *(f"  [{segment.index}] split_time" for segment in attempt.segments),
    ]


def runtime_state_lines(runtime: common_pb2.RuntimeState) -> list[str]:
    lines = [
        f"session={runtime.session_id} runtime_revision={runtime.runtime_revision}",
        f"timing_method={common_pb2.TimingMethod.Name(runtime.current_timing_method)} "
        f"comparison={runtime.current_comparison or '-'} "
        f"global_hotkeys={runtime.global_hotkeys_enabled}",
    ]
    lines.extend(
        f"  custom_variable {name}={value}"
        for name, value in runtime.custom_variables.items()
    )
    return lines


def print_message(message: object, as_json: bool) -> None:
    if as_json:
        print(json.dumps(message_dict(message), ensure_ascii=False, indent=2))
        return
    if isinstance(message, common_pb2.TimerState):
        print("\n".join(timer_state_lines(message)))
    elif isinstance(message, common_pb2.AttemptState):
        print("\n".join(attempt_lines(message)))
    elif isinstance(message, common_pb2.RuntimeState):
        print("\n".join(runtime_state_lines(message)))
    elif isinstance(message, run_pb2.RunState):
        print("\n".join(run_lines(message)))
    else:
        print(message)


def run_events(endpoint: str, as_json: bool, count: int | None) -> int:
    socket = websocket.create_connection(endpoint, timeout=None)
    received = 0
    print(f"Monitoring {endpoint} (Ctrl+C to stop)", file=sys.stderr)
    try:
        while count is None or received < count:
            data = socket.recv()
            if isinstance(data, str):
                continue
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
    finally:
        socket.close()
    return 0


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    if args.timeout <= 0:
        parser().error("--timeout must be greater than zero")
    if not 1 <= args.port <= 65535:
        parser().error("--port must be between 1 and 65535")

    match args.command:
        case "events":
            return run_events(events_url(args.port), args.json, args.count)

    try:
        with BridgeClient(rpc_url(args.port), round(args.timeout * 1000)) as client:
            match args.command:
                case "attach":
                    response = client.attach()
                    print_message(
                        response if args.json else response.attach.timer_state,
                        args.json,
                    )
                case "timer-state":
                    response = client.timer_state()
                    print_message(
                        response if args.json else response.get_timer_state.timer_state,
                        args.json,
                    )
                case "run":
                    response = client.run()
                    print_message(
                        response if args.json else response.get_run.run,
                        args.json,
                    )
                case "attempt":
                    response = client.attempt()
                    print_message(
                        response if args.json else response.get_attempt.attempt,
                        args.json,
                    )
                case "runtime":
                    response = client.runtime_state()
                    print_message(
                        response
                        if args.json
                        else response.get_runtime_state.runtime_state,
                        args.json,
                    )
                case "timer":
                    response = client.timer(TIMER_OPERATIONS[args.operation])
                    print_message(
                        response if args.json else response.operation, args.json
                    )
                    if not response.operation.success:
                        return 2
                case "game-time":
                    match args.operation:
                        case "set":
                            if args.seconds is None:
                                parser().error("game-time set requires seconds")
                            ticks = round(args.seconds * TICKS_PER_SECOND)
                            response = client.game_time("SET", ticks)
                        case operation:
                            if args.seconds is not None:
                                parser().error(
                                    f"game-time {operation} does not accept seconds"
                                )
                            response = client.game_time(GAME_TIME_OPERATIONS[operation])
                    print_message(
                        response if args.json else response.operation, args.json
                    )
                    if not response.operation.success:
                        return 2
    except (BridgeClientError, websocket.WebSocketException, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
