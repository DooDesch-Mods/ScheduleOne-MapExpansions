# Changelog

All notable changes to MapExpansions are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

## [1.4.1] - 2026-11-17

### Fixed
- The log no longer gets a line every time a worker walks between the old and the new floor of an expansion.

## [1.4.0] - 2026-11-17

### Added
- New rooms of an expansion can have their own wall switch, synced in co-op.
- The ambient sound of a property covers its new rooms.

### Fixed
- Co-op: a friend joining while a large build applies no longer times out.
- Co-op: a joining player no longer misses parts of a copied building, such as a sliding door, that the host sees.
- Walls of new rooms no longer look frayed or speckled under light.

### For mod authors
- New `ObjectEffect.ClearDetails` clears grass and bush tufts from a rule's area; `Remove` now also takes map trees.
- New `WorldStateDefinition.WeatherCover`: volumes under a roof without a floor, such as a carport, keep the rain out.
- `LightSwitch = "copy:<id>"` makes a copy's lamps follow a networked switch copy in the same world state.
- New `MapExpansionsApi.ReportPeerWorldStateProgress` keeps a join alive while the peer still applies a large state.
- `MapExpansionsApi.ApiVersion` is 0.7.0.

## [1.3.0] - 2026-11-16

### Fixed
- Co-op: joining a host whose property has an expanded floor or an added loading dock no longer disconnects after
  25 seconds.
- Parts of an expanded building no longer vanish or hide the building behind them when seen from close by.

### For mod authors
- New `PropertyExtensionDefinition.GridOrigin`: an extension builds its own floor grid at another height, with save
  and load, employee reach and the temperature view.
- New `CopyDefinition.Tilt`: tilts a copy placed with `At` in its own axes after its yaw, such as a rail along a stair.
- A world-state `Cut` rule now cuts its `Openings` as further areas in the same pass.
- A world state no longer fails to apply when a turned object far away stands next to it.
- `MapExpansionsApi.ApiVersion` is 0.6.0.

## [1.2.0] - 2026-11-06

### Added
- Floor under a moved room object, like the sink in Based's Sweatshop wing, opens for building as soon as another
  mod removes that object.

### For mod authors
- New `PropertyFixtureDefinition` in `PropertyExtensionDefinition.Fixtures`: tiles stay closed while their object
  stands, and open when it is removed, hidden or moved.
- New `MapExpansionsApi.IsFixture` and `SetFixtureReleased` (API 0.5.0) for a mod that takes such an object out of use.
- New `ObjectMoveDefinition.Optional`: a move whose object is missing is skipped instead of failing the world state.

## [1.1.0] - 2026-11-05

### Fixed
- Pay phones in expansions such as Serena Flats ring for a waiting call and can be answered.

### For mod authors
- New `WorldStateDefinition.NavigationExistingFloor`: floor the world's NavMesh already covers stays out of the
  state bake and is joined along its edges every 0.75 m.

## [1.0.4] - 2026-11-04

### Fixed
- Rain sounds muffled under an expansion's roof, like in any other building. It played as if you stood outside.
- Botanists use spawn stations in a freshly finished expansion room. They ignored them there before.

## [1.0.3] - 2026-11-03

### Fixed
- Rain stays out of a finished expansion room. It fell through the new roof, because the game's rain map does
  not know it.

## [1.0.2] - 2026-11-03

### Fixed
- An expansion finishes, and saving works, when something stands in the new room's walkway. Based blocked saving
  until the object moved.

### For mod authors
- A navigation probe without a path from the doorway is now a warning that names the colliders there; the world
  state still applies. Loose physics props stay out of the state bake.

## [1.0.1] - 2026-11-02

### Changed

- Thunderstore and Nexus Mods may now review the mod files: the license names their package review as an exception.

## [1.0.0] - 2026-11-02

### Added
- Mods like Based use this to add map areas and property upgrades. On its own it adds nothing you can see.

### For mod authors
- `MapExpansionsApi.Register(ExpansionDefinition)`: new areas, connections, regions and a sheet on the phone map.
- NPC and vehicle navigation, buildings, parking lots, deal locations, shops and a police station per area.
- Properties: copy one to a new place, or extend one with new floor, grids and docks.
- World states: `TrySetWorldState` switches named states at runtime and takes them back, also in co-op.
- Edits: hide, cut or copy main-world objects, in code or from a JSON layer.
