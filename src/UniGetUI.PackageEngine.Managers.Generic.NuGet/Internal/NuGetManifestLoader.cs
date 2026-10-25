using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PowerShellManager;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal static class NuGetManifestLoader
    {
        /// <summary>
        /// Returns the URL to the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <returns>A Uri object</returns>
        public static Uri GetManifestUrl(IPackage package)
        {
            return new Uri(
                $"{package.Source.Url}/Packages(Id='{package.Id}',Version='{package.VersionString}')"
            );
        }

        /// <summary>
        /// Returns the URL to the NuPkg file
        /// </summary>
        /// <param name="package">A valid Package object</param>
        /// <returns>A Uri object</returns>
        public static Uri GetNuPkgUrl(IPackage package)
        {
            if (NuGetV3ServiceIndex.IsV3Source(package.Source))
            {
                if (
                    NuGetV3ServiceIndex.Resolve(package.Source) is { } index
                    && NuGetV3Client.GetPackageContentUrl(
                        index,
                        package.Id,
                        package.VersionString
                    )
                        is { } contentUrl
                )
                    return contentUrl;

                throw new InvalidOperationException(
                    $"Could not resolve a V3 package content address for {package.Id} "
                        + $"{package.VersionString} on source {package.Source.Url}"
                );
            }

            return new Uri($"{package.Source.Url}/package/{package.Id}/{package.VersionString}");
        }

        /// <summary>
        /// Returns the contents of the manifest of a NuGet-based package
        /// </summary>
        /// <param name="package">The package for which to obtain the manifest</param>
        /// <returns>A string containing the contents of the manifest</returns>
        public static string? GetManifestContent(IPackage package)
        {
            if (BaseNuGet.Manifests.TryGetValue(package.GetVersionedHash(), out string? manifest))
            {
                Logger.Debug(
                    $"Loading cached NuGet manifest for package {package.Id} on manager {package.Manager.Name}"
                );
                return manifest;
            }

            string? PackageManifestContent = "";
            string PackageManifestUrl = GetManifestUrl(package).ToString();

            try
            {
                using (HttpClient client = new(CoreTools.GenericHttpClientParameters))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    HttpResponseMessage response = client
                        .GetAsync(PackageManifestUrl)
                        .GetAwaiter()
                        .GetResult();
                    if (!response.IsSuccessStatusCode && package.VersionString.EndsWith(".0"))
                    {
                        response = client
                            .GetAsync(new Uri(PackageManifestUrl.ToString().Replace(".0')", "')")))
                            .GetAwaiter()
                            .GetResult();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Warn(
                            $"Failed to download the {package.Manager.Name} manifest at Url={PackageManifestUrl.ToString()} with status code {response.StatusCode}"
                        );
                        return null;
                    }

                    PackageManifestContent = response
                        .Content.ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();
                }
                BaseNuGet.Manifests[package.GetHash()] = PackageManifestContent;
                return PackageManifestContent;
            }
            catch (Exception e)
            {
                Logger.Warn(
                    $"Failed to download the {package.Manager.Name} manifest at Url={PackageManifestUrl.ToString()}"
                );
                Logger.Warn(e);
                return null;
            }
        }

        private static string? CacheManifestContent(
            IPackage package,
            string packageManifestUrl,
            HttpResponseMessage response
        )
        {
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn(
                    $"Failed to download the {package.Manager.Name} manifest at Url={packageManifestUrl} with status code {response.StatusCode}"
                );
                return null;
            }

            string packageManifestContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            BaseNuGet.Manifests[package.GetVersionedHash()] = packageManifestContent;
            return packageManifestContent;
        }
    }
}
