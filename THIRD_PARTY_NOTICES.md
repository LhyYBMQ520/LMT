# Third-party components

LMT uses the packages pinned in the web pnpm-lock.yaml, Go go.sum, and desktop packages.lock.json. Their licenses and notices remain those of their respective authors.

Direct components:

- SIPSorcery / SIPSorceryMedia.Abstractions 10.0.17: see `licenses/SIPSorcery-LICENSE.md`. The package license includes additional use restrictions; retain and review the complete text when distributing. LMT does not use SIPSorceryMedia.FFmpeg.
- Concentus 2.2.2: see `licenses/Concentus-LICENSE.txt` (BSD terms and Opus notices).
- Gorilla WebSocket 1.5.3: see `licenses/Gorilla-WebSocket-LICENSE.txt` (BSD terms).
- React / React DOM, Vite, TypeScript and the React Vite plugin: MIT; full package notices are in their distributed packages. Vite and TypeScript are build tools.

Transitive packages, including cryptography and JSON dependencies, carry their own notices. Preserve the corresponding package licenses when redistributing binaries; do not describe the SIPSorcery license as an unmodified BSD license.
