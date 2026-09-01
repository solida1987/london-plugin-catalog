"""SC2 London Bridge — a headless driver for the sc2 world's own client.

Registers one Launcher component and no world. The component builds the sc2
world's SC2Context WITHOUT the kivy GUI, connects it to the multiworld, and
then takes orders on stdin:

    PLAY <mission_id>    launch that mission (validated by the world's own
                         is_mission_available — an unlocked check we never
                         have to reimplement)
    EXIT                 disconnect and quit

Status goes to stdout, one line each, launcher-pipe style:

    STATE:starting / STATE:connected / STATE:ready
    BOARD:<n missions>          after the mission order arrives
    PLAY:accepted:<id> / PLAY:refused:<id>
    LOG:<text>

The heavy machinery — the bot that runs the mission and applies items — lives
in the sc2 world inside this Archipelago install. This file only drives it.
"""

def launch_bridge(*args: str) -> None:
    from .bridge import run
    run(args)


try:
    from worlds.LauncherComponents import Component, components, Type

    components.append(Component(
        "SC2 London Bridge",
        func=launch_bridge,
        component_type=Type.HIDDEN,
    ))
except Exception:
    # An Archipelago too old to have LauncherComponents cannot use the bridge;
    # failing the whole world load would take the rest of custom_worlds down.
    pass
