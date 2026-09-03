# V6 MCP stage 4: Mixer and audio effects

Stage 4 is registered as the `mixer_effects` domain and is exposed through the
existing `v6_query_project`, `v6_get_catalog`, and `v6_apply_operations`
contracts. It does not add a dedicated bridge method or server tool.

## Queries

- `kind: "mixer"` reads every track's tick-zero volume/pan value, mute/solo
  flags, native ranges, and the number of automation points. Volume and pan are
  explicitly reported as either `automation_point_at_tick_zero` or
  `track_default`; later curve points are never presented as the static value.
- `kind: "effect_chains"` reads master, track, and part chains. `target` can
  narrow the result to `master`, `track`, or `part`. Effects include their real
  V6 GUID, order, bypass state, virtual-effect marker, and stored parameters.
- `kind: "effect_catalog"` enumerates the eleven V6 6.13 effect types and uses
  `WEffectController.GetGuid` plus the installed engine catalog. Parameter
  ranges, defaults, and units are returned only when V6 already has an
  initialized controller; the query never loads an effect merely to invent a
  schema.

## Unified operations

Use `domain: "mixer_effects"` in `v6_apply_operations`:

- `set_track_static`: `track_index` plus any of `volume`, `pan`, `mute`, or
  `solo`.
- `insert_effect`: `target`, target indexes, `effect_guid`, and optional `index`.
- `remove_effect`: target and `effect_index`.
- `move_effect`: target, `effect_index`, and `to_index`.
- `clear_effects`: target; preserves V6 virtual audio-part effects.
- `set_bypass`: target, `effect_index`, and `bypass`.
- `set_parameters`: target, `effect_index`, and a `parameters` object containing
  normalized float values in `[0, 1]`.

Dry-run resolves targets, validates ranges, installed GUIDs, virtual effects,
and parameter names/types without changing VSM or loading effects. A request is
executed within the existing single V6 `Transaction`, so moving a chain and a
batch of parameter changes each produce one undo step; any rejected operation
rolls the transaction back.

## Capability and safety boundaries

All stage-4 capability entries include the 6.13 minimum version and are marked
`host_validation_required` until verified inside the editor. Missing effect
engine state, an uninstalled or mismatched GUID, an absent target manager, and
an unavailable controller schema are reported explicitly rather than guessed.

Track mute/solo writes deliberately follow V6 6.13's own non-history contract.
The adapter invokes the confirmed `MixerViewModel` synchronization path, which
updates the track flag, Mixer UI, and audio input routing, then marks the
sequence as modified outside edit history. Query results expose
`mute_solo_undoable: false`; clients restore these flags by reading and writing
their previous values rather than by calling project undo/redo.

Host validation still needs to confirm UI/playback/mixdown synchronization,
undo/redo for history-backed values, direct mute/solo restoration, rollback on an intentionally invalid final operation, and all three
effect-chain target levels. No project deployment or user configuration change
is part of this stage.
