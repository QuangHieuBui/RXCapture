# Video recording: open at once, encode on Save (Snagit-style)

Status: draft for review. Date: 2026-10-16.

## Problem
After a recording stops, RXCapture converts the temporary AVI to MP4 in a blocking "Converting to MP4…" box and only then opens the
video in the editor. The wait is long for long clips, and the editor does not appear straight away.

## What Snagit does (from its COM samples and behaviour)
Snagit does not encode when recording stops. The capture opens in the Snagit Editor at once in a working format, and MP4 (H.264/AAC)
is written only when the user clicks Save / Finish. The TechSmith repository contains COM automation samples, not a recorder to copy.

## Goal
Stop recording -> the video is in the editor and playing within half a second, whatever its length. MP4 / GIF is produced only on Save.

## Not in scope
Direct-to-MP4 recording (rejected: crash risk, CPU load during capture, live audio sync). No change to the capture loop, the recorder bar,
the audio recorder, fps / quality settings, GIF writer or the MP4 reader.

## Design

### 1. Recording result is a working copy
- `RecorderForm.Result` already carries `AviPath` (+ `WavPath`). `VideoRecorder.Finish` no longer converts, whatever the saved-format setting is.
- The AVI (and the WAV when there is sound) are moved into the library folder as `<id>.avi` and `<id>.wav`.
- `LibraryStore.AddVideo(videoFile, firstFrame, seconds, wavFile = null)`: the item gets `Ext = "avi"`; "has sound" is derived from the
  presence of `<id>.wav` (no new metadata file); thumbnail from the first frame as today.
- The existing format setting (mp4 / gif) now only chooses the default type in the Save dialog.

### 2. Playing a working copy
- `VideoPlayerPanel.Open` already reads any file with Media Foundation; AVI (MJPEG) plays and seeks like MP4.
- Sound: `AudioPlayer.TryOpen(path)` also accepts a `.avi` that has a sibling `.wav` and reads the PCM from it (a WAV-backed reader
  implementing the same interface as `Mp4Writer.AudioReader`). Start / Stop / seek behave as today.
- The status bar says the recording is ready and that Save creates the MP4 (the title is unchanged, as for an unsaved image).

### 3. Save / Save As encodes
- `EditorForm.SaveVideoAs` (and Save when the item has no main file): the dialog offers MP4 and GIF (default from settings; sound forces MP4,
  as today).
- MP4: `Mp4Writer.Convert(avi, mp4, fps, wav)` inside the existing `BusyForm`; ffmpeg fallback unchanged; if no encoder exists, a message
  says so and the AVI stays.
- GIF: existing `AviToGif`.
- The result is written into the library as `<id>.mp4`, then copied to the chosen path. The item becomes an MP4 item
  (`File = <id>.mp4`, `Ext = "mp4"`, `ExportPath` = chosen path), and **the working AVI and WAV are deleted** (the player is released first,
  as `OnLibraryDeleting` already does). If conversion fails, nothing is deleted.
- A later Save on an item that is already MP4 keeps today's behaviour (plain copy to the main file).

### 4. Trim
- `TrimVideo` on a working copy reads the AVI (+ WAV) and writes a new MP4 item, as it does for MP4 today. `Mp4Writer.Trim` gets a
  WAV-backed `IAudioSource` (the abstraction already exists) so sound is carried over.

### 5. Library housekeeping
- Deleting an item removes `.avi` and `.wav` too; the "rebuild a missing thumbnail" logic finds `.avi` items.
- Old MP4 items and GIF items are untouched.

## Risks and decisions
- Disk: a working AVI is several times bigger than the MP4 until it is saved. Accepted; it is deleted on Save and removed with the item.
- A missing WAV only means no sound; a missing AVI behaves like any missing video file.
- AVI size limits: the recorder already writes classic AVI; no change in limits.

## Verification
- Time from stop to the player showing the first frame is under 0.5 s for a 10 s Full HD clip (no conversion on that path).
- Record with sound, open the working copy, the player produces sound (loopback check, skipped when other sound plays) and seeks.
- Save as MP4: valid file, duration within one frame, AAC track present, picture upright; AVI and WAV are gone; the item now plays from MP4.
- Save when conversion fails (forced): the AVI and WAV remain and the item still plays.
- Trim of a working copy keeps the chosen range and its sound.
- Deleting an item removes its `.avi` / `.wav`.
- Existing suites still pass: `--selftest`, `--filetest`, `--interact`, `--videotest` (updated where they assumed an MP4 straight after recording).
