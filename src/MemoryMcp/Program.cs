using MemoryMcp.Hosting;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Relay;
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

return role switch
{
    ServerRole.Standalone => await StandaloneServer.RunAsync(args),
    ServerRole.Relay => await RelayCommand.RunAsync(configuration, args),
    ServerRole.Service => await ServiceCommand.RunAsync(configuration, args),
    ServerRole.Start => await StartCommand.RunAsync(configuration),
    ServerRole.Stop => await StopCommand.RunAsync(configuration),
    ServerRole.Status => await StatusCommand.RunAsync(configuration),
    _ => throw new InvalidOperationException($"Role '{role}' has no command."),
};
