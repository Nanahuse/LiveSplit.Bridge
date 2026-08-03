from livesplit_bridge_cli.cli import TICKS_PER_SECOND, format_ticks


def test_format_ticks() -> None:
    assert format_ticks(12 * TICKS_PER_SECOND + 3_450_000) == "0:00:12.345"
    assert format_ticks(-TICKS_PER_SECOND) == "-0:00:01.000"
