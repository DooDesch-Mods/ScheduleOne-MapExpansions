# Changelog

All notable changes to MapExpansions are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

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
