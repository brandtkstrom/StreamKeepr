using System.Collections.Immutable;

namespace StreamKeepr.Domain;

/// <summary>The reducer result: the next state and the commands to execute in order.</summary>
public readonly record struct Step(DvrState Next, ImmutableArray<DvrCommand> Commands);
