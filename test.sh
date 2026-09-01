#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

live_development=false
user_filter=""
dotnet_args=()

while (($# > 0)); do
  case "$1" in
    --live-development)
      live_development=true
      shift
      ;;
    --filter)
      if (($# < 2)); then
        echo "--filter requires a value." >&2
        exit 2
      fi
      user_filter="$2"
      shift 2
      ;;
    --filter=*)
      user_filter="${1#--filter=}"
      shift
      ;;
    *)
      dotnet_args+=("$1")
      shift
      ;;
  esac
done

if [[ "$live_development" == true ]]; then
  if [[ -n "$user_filter" ]]; then
    echo "--filter cannot be combined with --live-development." >&2
    exit 2
  fi
  if [[ "${PATRON_REGISTRATION_LIVE_TESTS:-}" != "true" ]]; then
    echo "--live-development requires PATRON_REGISTRATION_LIVE_TESTS=true." >&2
    exit 2
  fi
  dotnet test src/Clc.PatronRegistration.Web.Tests/Clc.PatronRegistration.Tests.csproj \
    --filter "TestCategory=LiveDevelopment" "${dotnet_args[@]}"
  exit $?
fi

if [[ "$user_filter" == *LiveDevelopment* ]]; then
  echo "Raw filters may not mention LiveDevelopment; deterministic exclusion is mandatory." >&2
  exit 2
fi

node --check src/Clc.PatronRegistration.Web/wwwroot/js/settings.js
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/settings-edit-session.test.mjs
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/age-block.test.mjs
node --test src/Clc.PatronRegistration.Web.Tests/JavaScript/registration-branch-switch.test.mjs

if [[ -n "$user_filter" ]]; then
  user_filter="(${user_filter})&TestCategory!=LiveDevelopment"
else
  user_filter="TestCategory!=LiveDevelopment"
fi
dotnet test src/Clc.PatronRegistration.sln --filter "$user_filter" "${dotnet_args[@]}"
