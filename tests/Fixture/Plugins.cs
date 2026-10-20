using System;
using Microsoft.Xrm.Sdk;
namespace Fixture
{
    // Deliberately throw if executed: inspection must only read metadata.
    public abstract class PluginBase : IPlugin
    {
        protected PluginBase() { throw new Exception("Plugin code must never run during inspection."); }
        public void Execute(IServiceProvider serviceProvider) { throw new Exception("Do not run."); }
    }
    public sealed class ValidateRegistration : PluginBase { }
    public sealed class CalculateValues : PluginBase { }
    public sealed class UpdateStatus : PluginBase { }
    internal sealed class InternalPlugin : PluginBase { }
    public sealed class GenericPlugin<T> : PluginBase { }
    public sealed class NotAPlugin { }
}
