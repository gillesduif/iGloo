// Marker types for IStringLocalizer<T>. They sit in the root namespace so that
// Igloo.Fleet.Web.SharedStrings resolves to Resources/SharedStrings.resx.
namespace Igloo.Fleet.Web;

/// <summary>Actions, states and formats used on every page.</summary>
public sealed class SharedStrings { private SharedStrings() { } }

/// <summary>Header, navigation, breadcrumbs and account menu.</summary>
public sealed class ShellStrings { private ShellStrings() { } }

/// <summary>Every status label and its meaning, defined once (console-design.md, section 5).</summary>
public sealed class StatusStrings { private StatusStrings() { } }

public sealed class OverviewStrings { private OverviewStrings() { } }

public sealed class EnrollmentStrings { private EnrollmentStrings() { } }

public sealed class AccountStrings { private AccountStrings() { } }

/// <summary>Module pages (ModuleCatalog): field labels, create actions and states.</summary>
public sealed class ModuleStrings { private ModuleStrings() { } }

public sealed class SettingsStrings { private SettingsStrings() { } }

public sealed class PlanningStrings { private PlanningStrings() { } }

public sealed class AuditStrings { private AuditStrings() { } }
