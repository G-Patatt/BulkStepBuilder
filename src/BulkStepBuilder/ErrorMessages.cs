using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace BulkStepBuilder
{
    public static class ErrorMessages
    {
        public static string SigningRequired(string name) =>
            $"The DLL '{name}' is not fully strong-name signed. This tool registers individual assemblies, which require this signature.\n\n" +
            "What to do: in Visual Studio, open the plugin project's Properties > Signing, enable Sign the assembly, choose a strong-name key (.snk), and rebuild. Then select the rebuilt DLL again.\n\n" +
            "An Authenticode certificate is different from a strong-name key. You do not need to register the assembly manually in PRT first. No DLL or steps have been uploaded by this preview.";

        public static string Describe(Exception exception, string operation)
        {
            var ex = exception;
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            var details = TechnicalDetails(ex);
            var text = details.ToLowerInvariant();
            if (ex is InvalidOperationException && ex.InnerException == null)
                return operation + " could not be completed.\n\n" + ex.Message;
            string action;
            if (ContainsAny(text, "strong name", "strong-name", "publickeytoken", "public key token", "not fully signed", "not signed", "signature verification"))
                action = "Dataverse rejected assembly signing or key identity. Rebuild the DLL with a valid strong-name key. To update an existing assembly, use its original key. This tool does not sign or issue certificates for your DLL.";
            else if (ContainsAny(text, "certificate"))
                action = "Review the exact certificate requirement in the server details with your environment administrator. A certificate and a .NET strong-name key serve different purposes; this tool does not provision certificates.";
            else if (ex is TimeoutException || ContainsAny(text, "timeout", "timed out"))
                action = "The server did not confirm the result in time. Some changes may have been saved. Reconnect and generate a fresh preview before retrying; the tool checks existing registrations to avoid duplicates.";
            else if (ContainsAny(text, "privilege", "permission", "access is denied", "not authorized", "access denied", "insufficient access"))
                action = "The connected user may lack a required registration privilege. Ask your administrator to check access to plugin assemblies, plugin types and processing steps in the selected environment, then generate a fresh preview.";
            else if (ContainsAny(text, "could not load file or assembly", "filenotfoundexception", "dependency", "dependencies"))
                action = "Check the dependency named in the details. This tool uploads only the selected DLL; placing dependencies beside it enables inspection but does not deploy them. If dependent assemblies are required at runtime, deploy a supported plugin package with the appropriate tooling.";
            else if (ex is InvalidOperationException)
                return operation + " could not be completed.\n\n" + ex.Message;
            else
                action = "Review the server details below, correct the reported issue, and generate a fresh preview before trying again.";
            return operation + " failed.\n\nWhat to do: " + action + "\n\nServer / technical details: " + details;
        }

        private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);
        private static string TechnicalDetails(Exception exception)
        {
            var messages = new List<string>();
            for (var ex = exception; ex != null; ex = ex.InnerException)
            {
                messages.Add(ex.GetType().Name + ": " + ex.Message);
                if (ex is FaultException<OrganizationServiceFault> fault)
                {
                    for (var detail = fault.Detail; detail != null; detail = detail.InnerFault)
                        messages.Add($"Dataverse error 0x{unchecked((uint)detail.ErrorCode):X8}: {detail.Message}");
                }
            }
            return string.Join("\n", messages.Distinct());
        }
    }
}
