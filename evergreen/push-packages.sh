#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail
set +o xtrace # Disable tracing.

# querying nuget source to find search base url
packages_search_url=$(curl -X GET -s "${PACKAGES_SOURCE}" | jq -r 'first(.resources[] | select(."@type"=="SearchQueryService") | ."@id")')

wait_until_package_is_available ()
{
  package=$1
  version=$2
  resp=""
  count=0
  echo "Checking package availability: ${package}:${version} at ${packages_search_url}"
  while [ -z "$resp" ] && [ $count -le 40 ]; do
    resp=$(curl -X GET -s "$packages_search_url?prerelease=true&take=1&q=PackageId:$package" | jq --arg jq_version "$version" '.data[0].versions[] | select(.version==$jq_version) | .version')
    if [ -z "$resp" ]; then
      echo "sleeping for 15 seconds..."
      sleep 15
    fi
  done

  if [ -z "$resp" ]; then
    echo "Timeout while waiting for package availability: ${package}"
    exit 1
  else
    echo "Package ${package} is available, version: ${resp}"
  fi
}

if [ -z "$PACKAGES_SOURCE" ]; then
  echo "PACKAGES_SOURCE variable should be set"
  exit 1
fi

if [ -z "$PACKAGES_SOURCE_KEY" ]; then
  echo "PACKAGES_SOURCE_KEY variable should be set"
  exit 1
fi

if [ -z "$PACKAGE_VERSION" ]; then
  echo "PACKAGE_VERSION variable should be set"
  exit 1
fi

if [ -z "$NUGET_SIGN_CERTIFICATE_FINGERPRINT" ]; then
  echo "NUGET_SIGN_CERTIFICATE_FINGERPRINT variable should be set"
  exit 1
fi

dotnet nuget verify ./artifacts/nuget/MongoDB.EntityFrameworkCore."$PACKAGE_VERSION".nupkg --certificate-fingerprint "$NUGET_SIGN_CERTIFICATE_FINGERPRINT"
dotnet nuget push --source "$PACKAGES_SOURCE" --api-key "$PACKAGES_SOURCE_KEY" ./artifacts/nuget/MongoDB.EntityFrameworkCore."$PACKAGE_VERSION".nupkg

wait_until_package_is_available "MongoDB.EntityFrameworkCore" "$PACKAGE_VERSION"
