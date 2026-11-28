using MemoryMcp.Hosting;
using MemoryMcp.Shared;
using Microsoft.Extensions.Configuration;

ServerRole role;
try
{
    var configuration = ServerConfiguration.Build(args);
    var sharedOptions = configuration.GetSection(SharedOptions.SectionName).Get<SharedOptions>() ?? new SharedOptions();
    role = RoleSelector.Select(args, sharedOptions.Enabled);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

if (role == ServerRole.Standalone)
{
    return await StandaloneServer.RunAsync(args);
}

Console.Error.WriteLine($"Role '{role}' is not implemented yet.");
return 2;
