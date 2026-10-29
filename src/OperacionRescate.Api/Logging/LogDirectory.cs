namespace OperacionRescate.Api.Logging;

public static class LogDirectory
{
    public const string SettingName = "LogDirectory";

    /// <summary>
    /// Carpeta de los archivos de log: la configurada en "LogDirectory" o, si está vacía, la carpeta
    /// logs/ de la raíz del repositorio (donde está el .sln), sin depender de desde dónde se lance
    /// la aplicación (dotnet run fija el directorio de trabajo en el del proyecto).
    /// </summary>
    public static string Resolve(IConfiguration configuration, string contentRootPath)
    {
        var configurada = configuration[SettingName];
        if (!string.IsNullOrWhiteSpace(configurada))
        {
            return Path.GetFullPath(configurada);
        }

        for (var dir = new DirectoryInfo(contentRootPath); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.sln").Any())
            {
                return Path.Combine(dir.FullName, "logs");
            }
        }

        return Path.Combine(contentRootPath, "logs");
    }
}
