# Changelog
All notable changes to Unity Multi Timeline Recorder will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-06-02

### Added (fork: distributed rendering)
- LAN-distributed rendering: dispatch each selected Timeline as a job to Worker
  machines on the local network, run MTR's render pipeline headlessly on each
  Worker (ControlTrack-driven, honoring the MTR recorder settings), and collect
  results to `Recordings/Distributed/<YYYYMMDDHHMMSS>/<TimelineName>/`.
- HMAC-authenticated Master↔Worker HTTP transport, UDP worker discovery, and a
  Setup Wizard (`DistributedRecorder > Setup Wizard`) for password/registry/sync.
- Dispatch queue + retry: a single Worker records more Timelines than available
  workers sequentially — busy jobs are re-queued instead of failed.
- Sample generator: `DistributedRecorder > Create MTR Multi-Timeline Sample`.

## [1.0.0] - 2024-07-13

### Added
- Initial release of Unity Multi Timeline Recorder
- Multi-timeline batch recording functionality
- Support for multiple output formats:
  - Movie (MP4, MOV, WebM)
  - Image Sequences (PNG, JPG, EXR)
  - Animation Clips
  - Alembic
  - FBX
  - AOV (Arbitrary Output Variables)
- Flexible per-timeline recorder configuration
- Advanced path management with wildcard support
- Play Mode recording with real-time progress monitoring
- Comprehensive Editor UI for managing recordings
- Assembly definitions for proper code organization
- Full documentation and examples