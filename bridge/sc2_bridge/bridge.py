"""The bridge's main loop. See __init__ for the contract."""
import asyncio
import sys
import threading


def out(line: str) -> None:
    try:
        print(line, flush=True)
    except Exception:
        pass


def run(args) -> None:
    import colorama
    colorama.just_fix_windows_console()
    try:
        asyncio.run(main(list(args)))
    finally:
        colorama.deinit()


async def main(args) -> None:
    out("STATE:starting")

    # The sc2 world's own client, from THIS install — same code the kivy
    # window drives, minus the window.
    from worlds.sc2.client import SC2Context
    from CommonClient import server_loop, get_base_parser, handle_url_arg

    parser = get_base_parser()
    parser.add_argument('--name', default=None, help="Slot name to connect as.")
    parsed, rest = parser.parse_known_args(args)
    if rest and rest[0].startswith('archipelago://'):
        parsed.url = rest[0]
        handle_url_arg(parsed, parser)

    ctx = SC2Context(parsed.connect, parsed.password)
    ctx.auth = parsed.name
    ctx.server_task = asyncio.create_task(server_loop(ctx), name="ServerLoop")

    loop = asyncio.get_running_loop()
    commands: "asyncio.Queue[str]" = asyncio.Queue()

    def stdin_reader() -> None:
        # A thread, because sys.stdin has no async story on Windows. EOF on
        # the pipe means London is gone: shut down rather than orphan.
        for raw in sys.stdin:
            loop.call_soon_threadsafe(commands.put_nowait, raw.strip())
        loop.call_soon_threadsafe(commands.put_nowait, "EXIT")

    threading.Thread(target=stdin_reader, daemon=True, name="StdinReader").start()

    announced = False
    while not ctx.exit_event.is_set():
        # Announce readiness once the mission order has arrived — that is the
        # moment PLAY can mean anything.
        if not announced and getattr(ctx, "custom_mission_order", None):
            n = sum(len(col)
                    for campaign in ctx.custom_mission_order
                    for layout in campaign.layouts
                    for col in layout.missions)
            out(f"BOARD:{n}")
            out("STATE:ready")
            announced = True

        try:
            cmd = await asyncio.wait_for(commands.get(), timeout=0.5)
        except asyncio.TimeoutError:
            continue

        if cmd == "EXIT":
            out("STATE:closing")
            break
        if cmd.startswith("PLAY "):
            try:
                mission_id = int(cmd[5:].strip())
            except ValueError:
                out(f"LOG:bad mission id in {cmd!r}")
                continue
            if not announced:
                out(f"PLAY:refused:{mission_id} not connected yet")
                continue
            # The world's own gate: validates availability, cancels a
            # previous run, logs its own errors. It does NOT guard an id it
            # has never heard of — measured: play_mission(9999) is a raw
            # KeyError — so the guard lives here, where one bad command must
            # not take the whole session down.
            try:
                ok = ctx.play_mission(mission_id)
            except Exception as e:
                out(f"PLAY:refused:{mission_id} {type(e).__name__}: {e}")
                continue
            out(f"PLAY:{'accepted' if ok else 'refused'}:{mission_id}")
            continue
        out(f"LOG:unknown command {cmd!r}")

    await ctx.shutdown()
    out("STATE:closed")
