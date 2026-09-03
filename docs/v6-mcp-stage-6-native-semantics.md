# V6 MCP stage 6: native semantic editing

Stage 6 extends `v6_run_job`. These jobs invoke verified V6 6.13 business
methods; MCP does not reproduce their note adjacency, lyric/phoneme migration,
controller movement, selection notification, or transaction implementation.

Every request still requires `project_id`, `expected_revision`,
`client_request_id`, and the normal write lease. `dry_run: true` performs all
active Track/Part, selection, range, enum, and boundary checks but does not call
the V6 method. Its `impact` object reports the number of affected entities and
operation-specific details.

## Available jobs

- `transpose_note`: `options.semitones` in `[-12, 12]`, excluding zero.
- `staccato_note`: `options.strength` is `weak`, `medium`, or `strong`.
- `join_notes`: requires two or more selected notes in the active MIDI Part.
- `insert_rest`: requires `absolute_tick` inside the active MIDI Part and a
  positive `length_tick`; V6 moves notes and applicable controllers itself.
- `lyric_shift_left` / `lyric_shift_right`: require a contiguous selected-note
  range and reject shifts beyond the first/last note.
- `reset_lyrics`: requires selected notes on the active MIDI Track.
- `toggle_phonetic_protect`: requires selected notes on the active MIDI Track.
- `split_note`: requires `length_tick`, `base_position` (`note_on` or
  `note_off`), and `phoneme_strategy` (`melisma`, `specific_phoneme`, or
  `vowel`). `specific_phoneme` also requires `phonetic_symbol`. Both resulting
  note segments must be at least 30 ticks. Optional fields are `transpose`
  (`[-12, 12]`) and `protect_phonemes`.
- `join_parts`: calls V6 `TrackEditorViewModel.JoinMidiParts`, requires two or
  more selected MIDI Parts on a Track, and is unavailable during rendering.
- `duplicate_track`: calls V6 `TrackEditorViewModel.ExecuteDuplicateTrack`,
  requires selected Tracks, and validates the native total Track limit.
- `quantize_position`: requires selected notes and an active V6 quantize grid;
  `options.strength` is `full` or `half`. It executes V6's own Full/Half
  Quantize command, including its native transaction and collision rollback.
- `half_tempo` / `double_tempo`: require exactly one selected MIDI Part and
  call `TrackEditorViewModel.PartDoubleHalfTempo`. V6 scales note positions and
  durations, Direct Pitch timing, every Controller type, and Part duration in
  one transaction.
- `parameter_selection_reset`: calls the native selected-continuous-controller
  reset. Each selected run keeps its first point at the type default and removes
  the remaining points; V6's three HMM-only parameter types are excluded by the
  native method.
- `parameter_range_delete`: deletes an inclusive Part-relative
  `start_tick`..`end_tick` range for the parameter type currently active in the
  Parameter panel. Note-based Velocity/Mouth bars are rejected because their
  native deletion path is a different UI primitive.
- `insert_lyrics_batch`: requires a contiguous selected-note range and
  `options.lyrics`. It calls `WIVSMNote.SetLyricsAndResetPhonemes`, the same
  native semantic entry used after V6's Insert Lyrics dialog, so token
  distribution, G2PA context, phoneme reset, transaction, and resulting
  selection remain owned by V6.

These V6 methods own their transaction and selection-notification lifecycle, so
one job produces one native undo step. After execution MCP refreshes the editor;
the normal revision observer publishes the resulting revision event.

## Deliberately unavailable

`normalize_note` remains unavailable because the public V6 method accepts
precomputed removal and duration-change lists that the UI dialog command builds
internally. Reimplementing that calculation would violate the native-semantic
contract. V6 6.13 has no native duration-quantize business entry, and its
parameter translate/scale/clamp implementations are gesture behaviors rather
than callable semantic operations. Those catalog entries remain `unsupported`
with the more precise probe reason; start-position quantize, Part tempo scaling,
parameter reset, and parameter deletion are now backed by the native entries
listed above.

The UI's lyric extraction routine has a public read helper and should be
surfaced through a query rather than a mutating job. No separate selected-note
"convert phonetics" command exists in 6.13; phonetic generation remains part of
native lyric mutation, so `phonetic_conversion` is deliberately unavailable.

All available entries are marked `host_validation_required` until exercised in
V6.13. Host validation should compare dry-run counts, execute once, undo, redo,
and confirm the active selection plus UI and rendering state. It must use a
disposable project and does not require deployment or changes to user config.
