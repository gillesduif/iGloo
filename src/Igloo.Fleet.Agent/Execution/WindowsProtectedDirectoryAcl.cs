using System.Security.AccessControl;
using System.Security.Principal;

namespace Igloo.Fleet.Agent.Execution;

// Intended machine-service policy. Real-host elevated verification is outstanding.
public sealed class WindowsProtectedDirectoryAcl : IProtectedDirectoryAcl
{
    private static readonly SecurityIdentifier SystemIdentity = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public void CreateProtected(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);
        foreach (var identity in new[] { SystemIdentity, Administrators })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    public bool VerifyFile(string path)
    {
        try
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                (owner != SystemIdentity && owner != Administrators)) return false;
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            return rules.Length == 2 && new[] { SystemIdentity, Administrators }.All(id => rules.Count(r => r.IdentityReference == id &&
                r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl) == 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public bool Verify(string path)
    {
        try
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                (owner != SystemIdentity && owner != Administrators)) return false;
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            return rules.Length == 2 && new[] { SystemIdentity, Administrators }.All(id => rules.Count(r => r.IdentityReference == id &&
                !r.IsInherited && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl &&
                r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) && r.PropagationFlags == PropagationFlags.None) == 1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
