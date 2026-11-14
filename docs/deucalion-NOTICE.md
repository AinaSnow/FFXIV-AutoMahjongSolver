# Bundled capture component

Deucalion 1.5.1, Copyright its upstream contributors, is an unmodified third-party
component licensed under GPL-3.0. It communicates with the plugin through its
published named-pipe protocol. The DLL, complete tagged upstream source archive
(including Cargo build files), and upstream LICENSE.md are shipped together here.

Project/source: https://github.com/ff14wed/deucalion/tree/1.5.1
Binary: https://github.com/ff14wed/deucalion/releases/tag/1.5.1
SHA-256 (DLL): 4ac5d10790baa319027108cb1aecfa74c4f8d0423464a31642b186fe507ca9b8
SHA-256 (source ZIP): 394c052e735c00891d1a6f391f244b2f7ec26aa3928be4dd5b33e4053747e9ab

The plugin verifies the DLL before loading it into its own game process. It first
tries an existing server and does not issue Deucalion's global Exit command.
No game packets are transmitted; subscriber commands only select local filters,
set a subscriber name and keep the local pipe alive. The native component owns
its own hook initialization and shutdown. Its compatibility with a particular
game build must still be checked in the server greeting and live capture.
