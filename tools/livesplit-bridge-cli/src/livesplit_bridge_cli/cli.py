from __future__ import annotations

import argparse
import json
import math
import os
import socket
import sys
from decimal import (
    MAX_EMAX,
    MIN_EMIN,
    ROUND_HALF_EVEN,
    Decimal,
    InvalidOperation,
    localcontext,
)

import websocket

from .client import UINT32_MAX, BridgeClient, BridgeClientError, is_uint32

DEFAULT_PORT = 54000
RPC_PATH = "/bridge/v4/rpc"
EVENTS_PATH = "/bridge/v4/events"
TICKS_PER_SECOND = 10_000_000


def rpc_url(port: int) -> str:
    return f"ws://127.0.0.1:{port}{RPC_PATH}"


def events_url(port: int) -> str:
    return f"ws://127.0.0.1:{port}{EVENTS_PATH}"


def decimal_argument(value: str) -> Decimal:
    try:
        return Decimal(value)
    except InvalidOperation as error:
        raise argparse.ArgumentTypeError("must be a decimal number") from error


def scaled_integer(value: Decimal, scale: int) -> int:
    """Scale a Decimal exactly, then round with Python round() semantics."""
    with localcontext() as context:
        context.prec = max(28, len(value.as_tuple().digits) + len(str(scale)) + 1)
        context.Emax = MAX_EMAX
        context.Emin = MIN_EMIN
        scaled = value * scale
        return int(scaled.to_integral_value(rounding=ROUND_HALF_EVEN))


def timeout_milliseconds(
    value: Decimal, argument_parser: argparse.ArgumentParser
) -> int:
    if not value.is_finite() or value <= 0:
        argument_parser.error("--timeout must be a finite number greater than zero")
    if value < Decimal("0.0005"):
        argument_parser.error("--timeout must round to at least 1 millisecond")

    try:
        seconds = float(value)
    except OverflowError, ValueError:
        argument_parser.error("--timeout is not supported by the socket")
    if not math.isfinite(seconds):
        argument_parser.error("--timeout is not supported by the socket")

    try:
        milliseconds = scaled_integer(value, 1000)
    except OverflowError, InvalidOperation:
        argument_parser.error("--timeout is not supported by the socket")
    if milliseconds < 1:
        argument_parser.error("--timeout must round to at least 1 millisecond")

    try:
        timeout_seconds = milliseconds / 1000
        with socket.socket() as probe:
            probe.settimeout(timeout_seconds)
    except OSError, OverflowError, ValueError:
        argument_parser.error("--timeout is not supported by the socket")
    return milliseconds


def game_time_ticks(seconds: Decimal, argument_parser: argparse.ArgumentParser) -> int:
    if not seconds.is_finite():
        argument_parser.error("game-time seconds must be finite")
    if not seconds:
        return 0

    adjusted = seconds.adjusted()
    if adjusted >= 12:
        argument_parser.error(
            "game-time seconds are outside the signed 64-bit tick range"
        )
    if adjusted <= -9:
        return 0

    ticks = scaled_integer(seconds, TICKS_PER_SECOND)
    if not -(1 << 63) <= ticks <= (1 << 63) - 1:
        argument_parser.error(
            "game-time seconds are outside the signed 64-bit tick range"
        )
    return ticks


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(
        description="Debug LiveSplit.Bridge JSON v4 over WebSocket"
    )
    result.add_argument(
        "--port",
        type=int,
        default=os.getenv("LIVESPLIT_BRIDGE_WEBSOCKET_PORT", str(DEFAULT_PORT)),
        help="WebSocket port of the bridge (default: 54000)",
    )
    result.add_argument(
        "--timeout",
        type=decimal_argument,
        default=Decimal("3"),
        help="RPC timeout in seconds (default: 3)",
    )
    result.add_argument(
        "--json", action="store_true", help="Print the complete JSON v4 message"
    )
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("timer-state", help="Get the current timer state")
    commands.add_parser("run", help="Get the currently loaded run state")
    commands.add_parser("attempt", help="Get the current attempt state")
    commands.add_parser("context", help="Get the current context state")
    commands.add_parser("completed-count", help="Get the completed attempt count")

    timer = commands.add_parser("timer", help="Execute a timer operation")
    timer.add_argument(
        "operation",
        choices=("start", "split", "skip", "undo", "reset", "pause", "resume"),
    )

    game_time = commands.add_parser("game-time", help="Execute a game-time operation")
    game_time.add_argument(
        "operation", choices=("initialize", "set", "pause", "resume")
    )
    game_time.add_argument(
        "seconds",
        type=decimal_argument,
        nargs="?",
        help="Game time in seconds (required by set)",
    )

    events = commands.add_parser("events", help="Monitor bridge events")
    events.add_argument(
        "--count", type=int, help="Exit after receiving this many events"
    )
    return result


def format_ticks(ticks: int) -> str:
    negative = ticks < 0
    value = abs(ticks)
    hours, remainder = divmod(value, 3600 * TICKS_PER_SECOND)
    minutes, remainder = divmod(remainder, 60 * TICKS_PER_SECOND)
    seconds = remainder / TICKS_PER_SECOND
    prefix = "-" if negative else ""
    return f"{prefix}{hours}:{minutes:02d}:{seconds:06.3f}"


def optional_ticks(value: dict[str, object], field: str) -> str:
    ticks = value[field]
    return "-" if ticks is None else format_ticks(int(ticks))


def timer_state_lines(state: dict[str, object]) -> list[str]:
    return [
        f"phase={state['phase']} split_index={state['split_index']}",
        f"real_time={format_ticks(int(state['real_time_ticks']))} "
        f"game_time={optional_ticks(state, 'game_time_ticks')} "
        f"game_time_initialized={state['is_game_time_initialized']} "
        f"game_time_paused={state['is_game_time_paused']}",
    ]


def time_value(value: dict[str, object]) -> str:
    return (
        f"real_time={optional_ticks(value, 'real_time_ticks')} "
        f"game_time={optional_ticks(value, 'game_time_ticks')}"
    )


def run_lines(run: dict[str, object]) -> list[str]:
    lines = [
        f"game={run['game_name'] or '-'} category={run['category_name'] or '-'}",
        f"comparisons={run['comparisons']}",
    ]
    for segment in run["segments"]:
        assert isinstance(segment, dict)
        lines.append(f"  [{segment['index']}] {segment['name']}")
        for comparison in segment["comparisons"]:
            assert isinstance(comparison, dict) and isinstance(comparison["time"], dict)
            lines.append(f"    {comparison['name']}: {time_value(comparison['time'])}")
    return lines


def attempt_lines(attempt: dict[str, object]) -> list[str]:
    lines = [f"attempt_count={attempt['attempt_count']}"]
    for segment in attempt["segments"]:
        assert isinstance(segment, dict) and isinstance(segment["split_time"], dict)
        lines.append(f"  [{segment['index']}] {time_value(segment['split_time'])}")
    return lines


def context_lines(context: dict[str, object]) -> list[str]:
    lines = [
        f"timing_method={context['current_timing_method']}",
        f"current_comparison={context['current_comparison'] or '-'}",
    ]
    variables = context["custom_variables"]
    assert isinstance(variables, dict)
    lines.extend(
        f"  custom_variable {name}={value}" for name, value in sorted(variables.items())
    )
    return lines


def print_response(response: dict[str, object], method: str, as_json: bool) -> None:
    if as_json:
        print(json.dumps(response, ensure_ascii=False, indent=2))
        return
    result = response["result"]
    if method == "get_timer_state":
        assert isinstance(result, dict)
        lines = timer_state_lines(result)
    elif method == "get_run":
        assert isinstance(result, dict)
        lines = run_lines(result)
    elif method == "get_attempt":
        assert isinstance(result, dict)
        lines = attempt_lines(result)
    elif method == "get_context_state":
        assert isinstance(result, dict)
        lines = context_lines(result)
    elif method == "get_completed_count":
        assert isinstance(result, dict)
        lines = [f"completed_count={result['completed_count']}"]
    else:
        lines = ["ok"]
    print("\n".join(lines))


def validate_event(event: object) -> dict[str, object]:
    if not isinstance(event, dict):
        raise BridgeClientError("Bridge returned a non-object Event")
    if not is_uint32(event.get("session_id"), nonzero=True):
        raise BridgeClientError("Bridge returned an invalid Event session_id")
    if not is_uint32(event.get("event_sequence"), nonzero=True):
        raise BridgeClientError("Bridge returned an invalid event_sequence")
    if not isinstance(event.get("type"), str):
        raise BridgeClientError("Bridge returned an Event without a type")
    if "timer_state" not in event:
        raise BridgeClientError("Bridge returned an Event without timer_state")
    timer_state = event.get("timer_state")
    if timer_state is not None and not isinstance(timer_state, dict):
        raise BridgeClientError("Bridge returned an invalid Event timer_state")
    return event


def run_events(endpoint: str, as_json: bool, count: int | None) -> int:
    try:
        event_socket = websocket.create_connection(endpoint, timeout=None)
    except (OSError, websocket.WebSocketException) as error:
        raise BridgeClientError(f"Failed to connect to {endpoint}: {error}") from error
    received = 0
    previous_session: int | None = None
    previous_sequence: int | None = None
    print(f"Monitoring {endpoint} (Ctrl+C to stop)", file=sys.stderr)
    try:
        while count is None or received < count:
            try:
                data = event_socket.recv()
            except (OSError, websocket.WebSocketException) as error:
                raise BridgeClientError(f"Events connection closed: {error}") from error
            if data is None:
                raise BridgeClientError("Events connection closed by Bridge")
            if not isinstance(data, str):
                raise BridgeClientError(
                    "Bridge returned a binary Event frame; text expected"
                )
            try:
                event = validate_event(json.loads(data))
            except (json.JSONDecodeError, UnicodeError) as error:
                raise BridgeClientError(
                    f"Bridge returned invalid Event JSON: {error}"
                ) from error

            session = int(event["session_id"])
            sequence = int(event["event_sequence"])
            if previous_session is not None and session != previous_session:
                print(
                    f"warning: Event session changed from {previous_session} "
                    f"to {session}",
                    file=sys.stderr,
                )
                previous_sequence = None
            elif previous_sequence is not None:
                expected = (
                    1 if previous_sequence == UINT32_MAX else previous_sequence + 1
                )
                if sequence != expected:
                    print(
                        f"warning: Event sequence gap in session {session}: "
                        f"expected {expected}, got {sequence}",
                        file=sys.stderr,
                    )
            previous_session = session
            previous_sequence = sequence

            if as_json:
                print(json.dumps(event, ensure_ascii=False, indent=2))
            else:
                print(f"[{sequence}] {event['type']}")
                if isinstance(event.get("timer_state"), dict):
                    print("  " + "\n  ".join(timer_state_lines(event["timer_state"])))
            received += 1
    except KeyboardInterrupt:
        return 0
    finally:
        try:
            event_socket.close()
        except OSError, websocket.WebSocketException:
            pass
    return 0


def main(argv: list[str] | None = None) -> int:
    argument_parser = parser()
    args = argument_parser.parse_args(argv)
    if not 1 <= args.port <= 65535:
        argument_parser.error("--port must be between 1 and 65535")
    if args.command == "events" and args.count is not None and args.count < 1:
        argument_parser.error("events --count must be greater than zero")

    timeout_ms = timeout_milliseconds(args.timeout, argument_parser)
    ticks: int | None = None
    if args.command == "game-time":
        if args.operation == "set":
            if args.seconds is None:
                argument_parser.error("game-time set requires seconds")
            ticks = game_time_ticks(args.seconds, argument_parser)
        elif args.seconds is not None:
            argument_parser.error(f"game-time {args.operation} does not accept seconds")

    try:
        if args.command == "events":
            return run_events(events_url(args.port), args.json, args.count)
        with BridgeClient(rpc_url(args.port), timeout_ms) as client:
            if args.command == "timer-state":
                method, response = "get_timer_state", client.timer_state()
            elif args.command == "run":
                method, response = "get_run", client.run()
            elif args.command == "attempt":
                method, response = "get_attempt", client.attempt()
            elif args.command == "context":
                method, response = "get_context_state", client.context()
            elif args.command == "completed-count":
                method, response = "get_completed_count", client.completed_count()
            elif args.command == "timer":
                method, response = "timer_operation", client.timer(args.operation)
            else:
                assert args.command == "game-time"
                method = "game_time_operation"
                response = client.game_time(args.operation, ticks)
            print_response(response, method, args.json)
    except (BridgeClientError, websocket.WebSocketException, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
