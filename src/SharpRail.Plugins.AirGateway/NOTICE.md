# Air Gateway plugin

Adapted from CommanderTvis's ThinkRail fork (Apache-2.0, see `licenses/Thinkrail-Apache-2.0.txt`): the Central CLI
adapter (`packages/shared/src/jbcentral.ts`), its lifecycle (`packages/server/src/auth/jbcentral.ts`), the
provider card (`apps/web/src/panels/JetBrainsAiCard.tsx`) and the test fixture (`e2e/fixtures/bin/central`),
reimplemented in C#. This plugin invokes the user's installed `central` CLI; it bundles no JetBrains software and
the CLI stays separately installed and licensed by JetBrains.

`assets/air-gateway.svg` is an original shield-and-check drawing for this plugin, dedicated to the project's
license. JetBrains, Air and Central are trademarks of JetBrains s.r.o.; the name identifies the integration and
does not imply endorsement.
