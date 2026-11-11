#!/bin/sh
# Download exact source versions concurrently without sharing output files.
set -eu
manifest=$1
destination=$2
mkdir -p "$destination"
destination=$(cd "$destination" && pwd)
# GNU xargs waits for workers and fails if any download fails. Keep each
# .dsc and its archives together, including when sources share an orig tarball.
xargs -r -d '\n' -n 1 -P 4 sh -eu -c '
    destination=$1
    package=$2
    mkdir -p "$destination/$package"
    cd "$destination/$package"
    apt-get source --download-only --yes "$package"
' download-source "$destination" < "$manifest"
