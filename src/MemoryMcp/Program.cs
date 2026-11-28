using MemoryMcp.Hosting;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Service;
using Microsoft.Extensions.Configuration;

IConfigurationRoot configuration;
ServerRole role;
try
{
    configuration = ServerConfiguration.Build(args);
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

if (role == ServerRole.Service)
{
    return await ServiceCommand.RunAsync(configuration, args);
}

Console.Error.WriteLine($"Role '{role}' is not implemented yet.");
return 2;
