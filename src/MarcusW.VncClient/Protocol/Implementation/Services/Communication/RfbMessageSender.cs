using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Utils;
using Microsoft.Extensions.Logging;

namespace MarcusW.VncClient.Protocol.Implementation.Services.Communication;

/// <summary>
///     A background thread that sends queued messages and provides methods to add messages to the send queue.
/// </summary>
public class RfbMessageSender : BackgroundThread, IRfbMessageSender
{
    private readonly RfbConnectionContext _context;
    private readonly ILogger<RfbMessageSender> _logger;

    private readonly BlockingCollection<QueueItem> _queue = new(new ConcurrentQueue<QueueItem>());

    private readonly ProtocolState _state;

    private volatile bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RfbMessageSender" />.
    /// </summary>
    /// <param name="context">The connection context.</param>
    public RfbMessageSender(RfbConnectionContext context) : base("RFB Message Sender")
    {
        _context = context;
        _state = context.GetState<ProtocolState>();
        _logger = context.Connection.LoggerFactory.CreateLogger<RfbMessageSender>();

        // Log failure events from background thread base
        Failed += (_, args) => _logger.LogWarning(args.Exception, "Send loop failed.");
    }

    /// <inheritdoc />
    public void StartSendLoop()
    {
        _logger.LogDebug("Starting send loop...");
        Start();
    }

    /// <inheritdoc />
    public Task StopSendLoopAsync()
    {
        _logger.LogDebug("Stopping send loop...");
        return StopAndWaitAsync();
    }

    /// <inheritdoc />
    public void EnqueueInitialMessages(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Enqueuing initial messages...");

        // Send initial SetEncodings
        Debug.Assert(_context.SupportedEncodingTypes != null, "_context.SupportedEncodingTypes != null");
        EnqueueMessage(new SetEncodingsMessage(_context.SupportedEncodingTypes), cancellationToken);

        // Request full framebuffer update
        EnqueueMessage(new FramebufferUpdateRequestMessage(false, new(Position.Origin, _state.RemoteFramebufferSize)),
            cancellationToken);
    }

    /// <inheritdoc />
    public void EnqueueMessage<TMessageType>(IOutgoingMessage<TMessageType> message,
        CancellationToken cancellationToken = default) where TMessageType : class, IOutgoingMessageType
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(RfbMessageSender));

        cancellationToken.ThrowIfCancellationRequested();

        var messageType = GetAndCheckMessageType<TMessageType>();

        // Add message to queue
        _queue.Add(new(message, messageType), cancellationToken);
    }

    /// <inheritdoc />
    public void SendMessageAndWait<TMessageType>(IOutgoingMessage<TMessageType> message,
        CancellationToken cancellationToken = default) where TMessageType : class, IOutgoingMessageType
    {
        // ReSharper disable once AsyncConverter.AsyncWait
        SendMessageAndWaitAsync(message, cancellationToken).Wait(cancellationToken);
    }

    /// <inheritdoc />
    public Task SendMessageAndWaitAsync<TMessageType>(IOutgoingMessage<TMessageType> message,
        CancellationToken cancellationToken = default) where TMessageType : class, IOutgoingMessageType
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(RfbMessageSender));

        cancellationToken.ThrowIfCancellationRequested();

        var messageType = GetAndCheckMessageType<TMessageType>();

        // Create a completion source and ensure that completing the task won't block our send-loop.
        TaskCompletionSource<object?> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Add message to queue
        _queue.Add(new(message, messageType, completionSource), cancellationToken);

        return completionSource.Task;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            SetQueueCancelled();
            _queue.Dispose();
        }

        _disposed = true;

        base.Dispose(disposing);
    }

    // This method will not catch exceptions so the BackgroundThread base class will receive them,
    // raise a "Failure" and trigger a reconnect.
    protected override void ThreadWorker(CancellationToken cancellationToken)
    {
        try
        {
            Debug.Assert(_context.Transport != null, "_context.Transport != null");
            ITransport transport = _context.Transport;

            // Iterate over all queued items (will block if the queue is empty)
            foreach (QueueItem queueItem in _queue.GetConsumingEnumerable(cancellationToken))
            {
                IOutgoingMessage<IOutgoingMessageType> message = queueItem.Message;
                IOutgoingMessageType messageType = queueItem.MessageType;

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    string? parametersOverview = message.GetParametersOverview();
                    _logger.LogDebug("Sending {messageName} message ({parameters})...", messageType.Name,
                        parametersOverview ?? "no parameters");
                }

                try
                {
                    // Write message to transport stream
                    messageType.WriteToTransport(message, transport, cancellationToken);
                    queueItem.CompletionSource?.SetResult(null);
                }
                catch (Exception ex)
                {
                    // If something went wrong during sending, tell the waiting tasks about it (so for example the GUI doesn't wait forever).
                    queueItem.CompletionSource?.TrySetException(ex);

                    // Send-thread should still fail
                    throw;
                }
            }
        }
        catch
        {
            // When the loop was canceled or failed, cancel all remaining queue items
            SetQueueCancelled();
            throw;
        }
    }

    private TMessageType GetAndCheckMessageType<TMessageType>() where TMessageType : class, IOutgoingMessageType
    {
        Debug.Assert(_context.SupportedMessageTypes != null, "_context.SupportedMessageTypes != null");

        TMessageType messageType = _context.SupportedMessageTypes.OfType<TMessageType>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Could not find {typeof(TMessageType).Name} in supported message types collection.");
        if (!_state.UsedMessageTypes.Contains(messageType))
        {
            throw new InvalidOperationException(
                $"The message type {messageType.Name} must not be sent before checking for server-side support and marking it as used.");
        }

        return messageType;
    }

    private void SetQueueCancelled()
    {
        _queue.CompleteAdding();
        foreach (QueueItem queueItem in _queue)
            queueItem.CompletionSource?.TrySetCanceled();
    }

    private class QueueItem(
        IOutgoingMessage<IOutgoingMessageType> message,
        IOutgoingMessageType messageType,
        TaskCompletionSource<object?>? completionSource = null)
    {
        public IOutgoingMessage<IOutgoingMessageType> Message { get; } = message;

        public IOutgoingMessageType MessageType { get; } = messageType;

        public TaskCompletionSource<object?>? CompletionSource { get; } = completionSource;
    }
}
