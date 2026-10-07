// The MAUI SDK injects these as implicit global usings; a plain net8.0 project does not. The ViewModels
// are copied from the MAUI head verbatim and rely on them (IServiceScopeFactory, CreateAsyncScope,
// GetRequiredService), so they are declared here once for the whole project.
global using Microsoft.Extensions.DependencyInjection;
