#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

node --check src/Clc.PatronRegistration.Web/wwwroot/js/settings.js
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/settings-edit-session.test.mjs
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/age-block.test.mjs
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/registration-branch-switch.test.mjs
dotnet test src/Clc.PatronRegistration.sln "$@"
