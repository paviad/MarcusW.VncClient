using System;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using JetBrains.Annotations;
using MarcusW.VncClient.Output;

namespace MarcusW.VncClient.Avalonia;

/// <summary>
///     Displays a remote screen using the RFB protocol.
/// </summary>
[PublicAPI]
public partial class VncView : RfbRenderTarget, IOutputHandler
{
    /// <summary>
    ///     Defines the <see cref="Connection" /> property.
    /// </summary>
    public static readonly DirectProperty<VncView, RfbConnection?> ConnectionProperty =
        AvaloniaProperty.RegisterDirect<VncView, RfbConnection?>(nameof(Connection), o => o.Connection,
            (o, v) => o.Connection = v);

    private RfbConnection? _connection;

    // Disposable for cleaning up after connection detaches
    private CompositeDisposable _connectionDetachDisposable = [];

    /// <summary>
    ///     Initializes static members of the <see cref="VncView" /> class.
    /// </summary>
    static VncView()
    {
        // Make the view focusable to receive key input events.
        FocusableProperty.OverrideDefaultValue(typeof(VncView), true);
    }

    public VncView()
    {
        InitSizing();
    }

    /// <summary>
    ///     Gets or sets the connection that is shown in this VNC view.
    /// </summary>
    /// <remarks>
    ///     Interactions with this <see cref="VncView" /> will be forwarded to the selected <see cref="RfbConnection" />.
    ///     In case this property is set to <see langword="null" />, no connection will be attached to this view.
    /// </remarks>
    public RfbConnection? Connection
    {
        get => _connection;
        set
        {
            // Detach view from previous connection
            if (_connection != null)
            {
                if (ReferenceEquals(_connection.RenderTarget, this))
                {
                    _connection.RenderTarget = null;
                }

                if (ReferenceEquals(_connection.OutputHandler, this))
                {
                    _connection.OutputHandler = null;
                }
            }

            _connectionDetachDisposable.Dispose();

            // Clear key input state
            ResetKeyPresses();

            // Attach view to new connection
            if (value != null)
            {
                _connectionDetachDisposable = [];

                value.RenderTarget = this;
                value.OutputHandler = this;

                // Make sure the connection is resized to fit the view, if enabled
                // Dispatch this to make sure the Connection property has been updated
                Dispatcher.UIThread.Post(SendInitialSizeUpdate);
            }

            SetAndRaise(ConnectionProperty, ref _connection, value);
        }
    }

    /// <inheritdoc />
    public virtual void RingBell()
    {
        // Ring the system bell
        Console.Beep();
    }

    /// <inheritdoc />
    public virtual void HandleServerClipboardUpdate(string text)
    {
        Dispatcher.UIThread.InvokeAsync(async () => {
            // Copy the text to the local clipboard
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not null)
            {
                await topLevel.Clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        });
    }
}
