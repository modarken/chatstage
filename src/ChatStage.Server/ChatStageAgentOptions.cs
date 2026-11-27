namespace ChatStage.Server;

/// <summary>Options for an agent served with <see cref="ChatStageServerExtensions.MapChatStageAgent(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, Microsoft.Agents.AI.AIAgent, Action{ChatStageAgentOptions})"/>.</summary>
public sealed class ChatStageAgentOptions
{
    /// <summary>
    /// Says what the user is told when a run fails with an exception. Return the message to show, or
    /// <see langword="null"/> to keep the exception private.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message goes to the client as the <c>message</c> of the AG-UI <c>RUN_ERROR</c> event that ends the run, and a
    /// ChatStage chat shows it in place of the reply. When this returns <see langword="null"/> (or is not set, or throws),
    /// the client gets only the AG-UI hosting's generic message: nothing about the exception leaves the server.
    /// </para>
    /// <para>
    /// Return text written for the user, typically the message of your own exception type:
    /// <c>ex => ex is MyUserFacingException ? ex.Message : null</c>. The exception is passed as the agent threw it, so
    /// unwrap an <see cref="AggregateException"/> or an inner exception yourself if you need to. Failed runs are logged
    /// either way.
    /// </para>
    /// </remarks>
    public Func<Exception, string?>? DescribeError { get; set; }
}
