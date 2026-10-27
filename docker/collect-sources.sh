#!/bin/sh
# Bundle exact Ubuntu source packages and installed copyright notices for the
# GPL/LGPL software redistributed in the image. Failure blocks publication.
set -eu
mkdir -p /corresponding-source/packages /corresponding-source/licenses
if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then
    sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/ubuntu.sources
else
    echo 'Expected Ubuntu deb822 sources; refusing an incomplete source bundle.' >&2
    exit 1
fi
apt-get update
# Exact source package versions come from the installed binary package metadata.
dpkg-query -W -f='${binary:Package}\t${source:Package}\t${source:Version}\n' > /corresponding-source/installed-packages.tsv
TAB=$(printf '\t')
while IFS="$TAB" read -r binary source version; do
    package=${binary%%:*}
    copyright="/usr/share/doc/$package/copyright"
    if [ -f "$copyright" ]; then
        cp -L "$copyright" "/corresponding-source/licenses/$package-copyright"
        if grep -Eiq 'GPL|General Public License' "$copyright"; then
            printf '%s=%s\n' "$source" "$version"
        fi
    else
        # Missing license metadata must not silently exclude a source package.
        printf '%s=%s\n' "$source" "$version"
    fi
done < /corresponding-source/installed-packages.tsv | sort -u > /corresponding-source/source-packages.txt
cd /corresponding-source/packages
while IFS= read -r package; do
    apt-get source --download-only --yes "$package"
done < /corresponding-source/source-packages.txt
printf '%s\n' 'Exact Ubuntu source packages (.dsc, original source and Debian/Ubuntu changes) are in packages/. Use dpkg-source -x package.dsc to extract, then the included debian/rules and build dependencies to rebuild. Installed package/source versions and copyright notices are included. The Media Server source is the release tag in the repository; project and browser component notices are also retained in the image under /app/licenses. Microsoft .NET runtime notices remain in the upstream base image.' > /corresponding-source/README.txt
cd /
tar -czf /Container-Corresponding-Source.tar.gz corresponding-source
