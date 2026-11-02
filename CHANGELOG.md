# Changelog

All notable changes to MapExpansions are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

## [1.0.0] - 2026-11-02

### Added
- Mods like Based use this to add map areas and property upgrades. On its own it adds nothing you can see.

### For mod authors
- `MapExpansionsApi.Register(ExpansionDefinition)`: new areas, connections, regions and a sheet on the phone map.
- NPC and vehicle navigation, buildings, parking lots, deal locations, shops and a police station per area.
- Properties: copy one to a new place, or extend one with new floor, grids and docks.
- World states: `TrySetWorldState` switches named states at runtime and takes them back, also in co-op.
- Edits: hide, cut or copy main-world objects, in code or from a JSON layer.
