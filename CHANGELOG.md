# Changelog

All notable changes to MapExpansions are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

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
