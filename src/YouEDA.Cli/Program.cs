using EasyEdaAltiumGrabber.Services;

if (args is ["--write-common-catalog-from-directory", var sourceDirectory, var destinationPath])
{
    var count = await UserSymbolLibraryResolver.WriteCommonCatalogFromDirectoryAsync(sourceDirectory, destinationPath);
    Console.WriteLine($"Created verified {count}-symbol native catalog: {destinationPath}");
    return;
}

Console.Error.WriteLine("Usage: YouEDA.Cli --write-common-catalog-from-directory <source-directory> <destination.SchLib>");
Environment.ExitCode = 2;
