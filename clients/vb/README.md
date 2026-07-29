# Logrr from VB.NET

Worked VB.NET examples for [`Logrr.Client`](../../src/Logrr.Client/README.md). The client
targets `netstandard2.0`, so everything here works from **.NET Framework 4.6.1 through
.NET 10** — WebForms and WinForms apps included.

`Program.vb` is a real project in the solution, built with `Option Strict On`, so these
examples cannot silently rot when the client API changes.

```bash
dotnet run --project clients/vb
```

It is safe to run without a server: logging is fire-and-forget, so unreachable-server
events are counted in `DroppedCount` rather than thrown.

## Install

```bash
dotnet add package Logrr.Client
```

On .NET Framework, install the same package from the NuGet UI in Visual Studio.

## Direct client (no logging framework)

The common case for VB apps. Create **one** client for the life of the process and share
it — it batches internally, so a client per call defeats the batching and leaks a timer
and an `HttpClient` each time.

```vb
Imports Logrr.Client

Using client As New LogrrClient(New LogrrClientOptions With {
        .Endpoint = "https://logrr.internal",
        .ApiKey = "lg_billing_xxxxxxxxxxxxxxxxxxxxxx",
        .MinimumLevel = LogrrLevel.Information
    })

    client.Log(LogrrLevel.Warning, "Cache miss for {Key}",
               properties:=New Dictionary(Of String, Object) From {
                   {"Key", "user:1042"}
               })

    Try
        ProcessPayment(49.99D, 1042)
    Catch ex As Exception
        client.Log(LogrrLevel.Error, "Payment {Amount} failed for {UserId}",
                   exception:=ex,
                   properties:=New Dictionary(Of String, Object) From {
                       {"Amount", 49.99D},
                       {"UserId", 1042}
                   })
    End Try
End Using
```

`Log` takes `(level, messageTemplate, exception, properties)`, the last two optional — use
named arguments (`properties:=`) to skip `exception` rather than passing `Nothing`
positionally. For full control over source, trace ids and timestamps, use `Emit`:

```vb
client.Emit(New LogrrEvent With {
    .Timestamp = DateTimeOffset.UtcNow,
    .Level = LogrrLevel.Information,
    .MessageTemplate = "Nightly reconciliation finished in {ElapsedMs} ms",
    .Source = "Billing.Reconciliation",
    .Properties = New Dictionary(Of String, Object) From {{"ElapsedMs", 1843}}
})
```

## Microsoft.Extensions.Logging

In a Generic Host app, one line — the extension method needs `Imports Logrr.Client.Logging`:

```vb
Imports Logrr.Client.Logging

builder.Logging.AddLogrr("https://logrr.internal", "lg_billing_...")
```

Without a host, build a factory directly:

```vb
Using factory As ILoggerFactory = LoggerFactory.Create(
    Sub(logging)
        logging.SetMinimumLevel(LogLevel.Information)
        logging.AddLogrr("https://logrr.internal", "lg_billing_...")
    End Sub)

    Dim logger As ILogger = factory.CreateLogger("Billing")
    logger.LogInformation("Invoice {InvoiceId} issued to {Customer}", 90210, "Acme Ltd")
    logger.LogError(ex, "Payment {Amount} failed for {UserId}", 49.99D, 1042)
End Using
```

The logger category (`"Billing"`) arrives as `SourceContext`.

## ASP.NET on .NET Framework (WebForms / MVC)

`HttpApplication` instances are pooled and recycled per request, so the client must be
`Shared` — an instance field would create one per pooled application object.

```vb
' Global.asax.vb
Imports System.Collections.Generic
Imports System.Web
Imports Logrr.Client

Public Class MvcApplication
    Inherits HttpApplication

    Friend Shared ReadOnly Logrr As New LogrrClient(New LogrrClientOptions With {
        .Endpoint = "https://lanticket.credit.com:5443",
        .ApiKey = "lg_tasktracker_xxxxxxxxxxxxxxxxxxxxxx"
    })

    Sub Application_Error(sender As Object, e As EventArgs)
        Dim ex As Exception = Server.GetLastError()
        If ex Is Nothing Then Exit Sub

        Dim props As New Dictionary(Of String, Object)
        Dim ctx As HttpContext = HttpContext.Current
        If ctx IsNot Nothing AndAlso ctx.Request IsNot Nothing Then
            props("Verb") = ctx.Request.HttpMethod
            props("Path") = ctx.Request.Path
            props("User") = If(ctx.User?.Identity?.Name, "(anonymous)")
        End If

        Logrr.Log(LogrrLevel.Error, "Unhandled error in {Verb} {Path}",
                  exception:=ex, properties:=props)
    End Sub

    Sub Application_End(sender As Object, e As EventArgs)
        ' Best-effort final flush before the app pool recycles.
        Logrr.Dispose()
    End Sub

End Class
```

If your app pool recycles aggressively, note that `Application_End` is not guaranteed to
run. Events buffered at that moment are lost — this is the fire-and-forget trade-off, not
a bug. Lower `FlushInterval` if you need a tighter bound.

## WinForms / WPF

Catch both the UI-thread and domain-level handlers, and dispose on the way out:

```vb
Imports System.Windows.Forms
Imports Logrr.Client

Module EntryPoint

    Friend ReadOnly Logrr As New LogrrClient(New LogrrClientOptions With {
        .Endpoint = "https://logrr.internal",
        .ApiKey = "lg_desktop_xxxxxxxxxxxxxxxxxxxxxx"
    })

    <STAThread>
    Sub Main()
        AddHandler Application.ThreadException, AddressOf OnUiException
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnFatalException

        Application.EnableVisualStyles()
        Try
            Application.Run(New MainForm())
        Finally
            Logrr.Dispose()   ' bounded final flush
        End Try
    End Sub

    Private Sub OnUiException(sender As Object, e As ThreadExceptionEventArgs)
        Logrr.Log(LogrrLevel.Error, "Unhandled UI exception", exception:=e.Exception)
    End Sub

    Private Sub OnFatalException(sender As Object, e As UnhandledExceptionEventArgs)
        Logrr.Log(LogrrLevel.Fatal, "Fatal exception", exception:=TryCast(e.ExceptionObject, Exception))
        Logrr.FlushAsync().GetAwaiter().GetResult()   ' the process is about to die
    End Sub

End Module
```

## Already using Serilog?

Point the Seq sink at Logrr and skip this package entirely — the wire format is the same.
VB needs explicit `_` line continuations for the fluent chain:

```vb
Serilog.Log.Logger = New LoggerConfiguration() _
    .WriteTo.Seq("https://logrr.internal/", apiKey:="lg_billing_...") _
    .CreateLogger()
```

## VB-specific notes

**Message templates, not interpolation.** This is the one that quietly destroys the value
of structured logging:

```vb
' WRONG - values are baked into the text before Logrr sees them. Every event gets a unique
' message so they never group, and there is no Amount or UserId to filter on.
client.Log(LogrrLevel.Information, $"Payment {amount} failed for {userId}")

' RIGHT - constant template, values arrive as typed, searchable properties.
client.Log(LogrrLevel.Information, "Payment {Amount} failed for {UserId}",
           properties:=New Dictionary(Of String, Object) From {
               {"Amount", amount}, {"UserId", userId}})
```

**Don't name your VB root namespace `Logrr.Something`.** VB prefixes every type with the
root namespace, so a root beginning with `Logrr` makes `Imports Logrr.Client` resolve
against your own project and fail with *"namespace or type 'Client' is not defined"*. Use
an unrelated root namespace, or write `Global.Logrr.Client` everywhere. This sample sets
`<RootNamespace>LogrrVbSample</RootNamespace>` for exactly that reason.

**VB has no `Async Sub Main`.** When you need a flush to complete before the next
statement, block explicitly:

```vb
client.FlushAsync().GetAwaiter().GetResult()
```

**Scopes are accepted but discarded.** `BeginScope` returns a no-op, so properties set on
a scope never reach the server. Anything you want to search on must be part of the event
itself — put correlation ids in the message template:

```vb
logger.LogWarning("Retry {Attempt} of {Max} for {CorrelationId}", 2, 3, correlationId)
```

**One client per process, not per call.** `LogrrClient` owns a timer and an `HttpClient`
and batches across calls. In ASP.NET make it `Shared`; with DI register it as a singleton.

**Levels.** `LogrrLevel` is `Verbose, Debug, Information, Warning, Error, Fatal`.
`LogrrLevel.Error` needs no bracket-escaping — member access is unambiguous with VB's
legacy `Error` statement.
