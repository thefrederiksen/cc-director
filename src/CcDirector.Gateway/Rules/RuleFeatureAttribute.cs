namespace CcDirector.Gateway.Rules;

/// <summary>
/// MARKS A TYPE AS PART OF SESSION RULES, so the guards that reason about the feature can find every piece
/// of it.
///
/// The guards used to select the feature by NAMESPACE, plus the two stored-row types picked out by name.
/// That was the feature as it stood when the guard was written, and the feature then grew outside it: the
/// rule endpoints live in the API namespace and the launch lived inside the Gateway host, and both were
/// listed as phase 2 feature pieces while sitting outside the thing guarding the feature. A guard whose
/// scope is narrower than what it claims to cover reports clean about code it never looked at.
///
/// A marker is used rather than a list kept in the test, because a list has to be remembered and a marker
/// travels with the type. Writing a new piece of this feature outside the rules namespace and forgetting
/// this attribute is still possible - nothing can stop that - but the attribute is one line at the top of
/// the file being written, rather than an edit in another project that nobody is looking at.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class RuleFeatureAttribute : Attribute
{
}
