using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GraphQL.Language.AST;
using GraphQL.Subscription;
using GraphQL.Types;

namespace GraphQL.Execution
{
    public class SubscriptionExecutionStrategy : ParallelExecutionStrategy
    {
        public override async Task<ExecutionResult> ExecuteAsync(ExecutionContext context)
        {
            var rootType = ExecutionHelper.GetOperationRootType(context.Document, context.Schema, context.Operation);
            var rootNode = BuildExecutionRootNode(context, rootType);

            var streams = await ExecuteSubscriptionNodesAsync(context, rootNode.SubFields).ConfigureAwait(false);

            ExecutionResult result = new SubscriptionExecutionResult
            {
                Streams = streams
            }.With(context);

            return result;
        }

        private async Task<IDictionary<string, IObservable<ExecutionResult>>> ExecuteSubscriptionNodesAsync(ExecutionContext context, IDictionary<string, ExecutionNode> nodes)
        {
            var streams = new Dictionary<string, IObservable<ExecutionResult>>();

            foreach (var kvp in nodes)
            {
                var name = kvp.Key;
                var node = kvp.Value;

                if (!(node.FieldDefinition is EventStreamFieldType))
                    continue;

                streams[name] = await ResolveEventStreamAsync(context, node).ConfigureAwait(false);
            }

            return streams;
        }

        protected virtual async Task<IObservable<ExecutionResult>> ResolveEventStreamAsync(ExecutionContext context, ExecutionNode node)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                var eventStreamField = node.FieldDefinition as EventStreamFieldType;

                IObservable<object> subscription;

                if (eventStreamField?.Subscriber != null)
                {
                    var resolveContext = eventStreamField.Subscriber is IResolveEventStreamContextProvider provider
                        ? provider.CreateContext(node, context)
                        : new ReadonlyResolveFieldContext<object>(node, context);
                    subscription = eventStreamField.Subscriber.Subscribe(resolveContext);
                }
                else if (eventStreamField?.AsyncSubscriber != null)
                {
                    var resolveContext = eventStreamField.AsyncSubscriber is IResolveEventStreamContextProvider provider
                        ? provider.CreateContext(node, context)
                        : new ReadonlyResolveFieldContext<object>(node, context);
                    subscription = await eventStreamField.AsyncSubscriber.SubscribeAsync(resolveContext).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException($"Subscriber not set for field '{node.Field.Name}'.");
                }

                return subscription
                    .Select(value =>
                    {
                        var executionNode = BuildExecutionNode(node.Parent, node.GraphType, node.Field, node.FieldDefinition, node.IndexInParentNode);
                        executionNode.Source = value;
                        return executionNode;
                    })
                    .SelectMany(async executionNode =>
                    {
                        if (context.Listeners != null)
                            foreach (var listener in context.Listeners)
                            {
                                await listener.BeforeExecutionAsync(context)
                                    .ConfigureAwait(false);
                            }

                        // Execute the whole execution tree and return the result
                        await ExecuteNodeTreeAsync(context, executionNode).ConfigureAwait(false);

                        if (context.Listeners != null)
                            foreach (var listener in context.Listeners)
                            {
                                await listener.AfterExecutionAsync(context)
                                    .ConfigureAwait(false);
                            }

                        return new ExecutionResult
                        {
                            Data = new Dictionary<string, object>
                            {
                                { executionNode.Name, executionNode.ToValue() }
                            }
                        }.With(context);
                    })
                    .Catch<ExecutionResult, Exception>(exception =>
                        Observable.Return(
                            new ExecutionResult
                            {
                                Errors = new ExecutionErrors
                                {
                                    GenerateError(
                                        context,
                                        $"Could not subscribe to field '{node.Field.Name}' in query '{context.Document.OriginalQuery}'.",
                                        node.Field,
                                        node.ResponsePath,
                                        exception)
                                }
                            }.With(context)));
            }
            catch (Exception ex)
            {
                var message = $"Error trying to resolve field '{node.Field.Name}'.";
                var error = GenerateError(context, message, node.Field, node.ResponsePath, ex);
                context.Errors.Add(error);
                return null;
            }
        }

        private ExecutionError GenerateError(
            ExecutionContext context,
            string message,
            Field field,
            IEnumerable<object> path,
            Exception ex = null) => new ExecutionError(message, ex) { Path = path }.AddLocation(field, context.Document);
    }
}
