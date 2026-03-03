#!/usr/bin/env bash
set -euo pipefail

# Script: upgrade-nuget-packages.sh
# Purpose: Apply recommended NuGet package upgrades for the AlpacaFleece C# projects,
# then restore, build, and run tests. Review output before committing.

BRANCH_NAME="chore/nuget-updates-$(date +%Y%m%d%H%M%S)"

echo "Creating git branch: ${BRANCH_NAME}"
git checkout -b "${BRANCH_NAME}"

echo "Updating packages..."

echo "Updating each PackageReference to latest for projects under src/ and tests/"

# Update all PackageReference entries in each project to the latest stable by running
# `dotnet add <proj> package <name>` for each PackageReference found in the csproj.
update_project_packages_to_latest() {
	proj="$1"
	echo "Scanning ${proj} for PackageReference entries..."
	# Extract Include="Package.Name" occurrences on lines with PackageReference
	mapfile -t pkgs < <(awk '/<PackageReference/{ if(match($0,/Include="[^"]+"/)) { s=substr($0,RSTART+9,RLENGTH-10); print s } }' "$proj")
	if [ ${#pkgs[@]} -eq 0 ]; then
		echo "  No PackageReference entries found in ${proj}, skipping."
		return
	fi
	for pkg in "${pkgs[@]}"; do
		echo "  Updating ${pkg} in ${proj} to latest..."
		dotnet add "$proj" package "$pkg"
	done
}

# Find projects
mapfile -t projects < <(find src tests -name '*.csproj' -print 2>/dev/null || true)
if [ ${#projects[@]} -eq 0 ]; then
	echo "No project files found under src/ or tests/; nothing to update."
else
	for p in "${projects[@]}"; do
		update_project_packages_to_latest "$p"
	done
fi

echo "Restore, build, and run tests"

dotnet restore

dotnet build

# Run tests (may fail if API changes are required after upgrades)
dotnet test

echo "If tests pass, review changes and commit. To commit:"
echo "  git add -A"
echo "  git commit -m 'chore(deps): upgrade NuGet packages (EF Core, Serilog, Polly, test SDK, etc.)'"

echo "If anything breaks, you can abort and restore previous branch with:"
echo "  git checkout main && git branch -D ${BRANCH_NAME}"

echo "Done."
