# Bundled capture component

Deucalion 1.5.0, Copyright its upstream contributors, is an unmodified third-party
component licensed under GPL-3.0. It communicates with the plugin through its
published named-pipe protocol. The DLL, complete tagged upstream source archive
(including Cargo build files), and upstream LICENSE.md are shipped together here.

Project/source: https://github.com/ff14wed/deucalion/tree/1.5.0
Binary: https://github.com/ff14wed/deucalion/releases/tag/1.5.0
SHA-256 (DLL): 326be4db261064ebb51b8c46d2940f55701d79887e2c65070aff00de4c8c65ef
SHA-256 (source ZIP): 07f664993a4a070cc44871752973a5b9c7632479750136eb5408f564a5c4d3ee

The plugin verifies the DLL before loading it into its own game process. It first
tries an existing server and does not issue Deucalion's global Exit command.
No game packets are transmitted; subscriber commands only select local filters,
set a subscriber name and keep the local pipe alive. The native component owns
its own hook initialization and shutdown. Its compatibility with a particular
game build must still be checked in the server greeting and live capture.
