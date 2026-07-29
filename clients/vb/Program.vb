Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports Logrr.Client
Imports Logrr.Client.Logging
Imports Microsoft.Extensions.Logging

''' <summary>
''' Worked VB.NET examples for shipping structured logs to Logrr, compiled as part of the
''' solution so they cannot drift from the client API.
'''
''' Safe to run without a server: the client is fire-and-forget, so a failed send is counted
''' in DroppedCount and never throws into the caller.
''' </summary>
Module Program

    ' Replace with your server and an ingest-scoped token from the Logrr UI (Apps -> Tokens).
    Private Const Endpoint As String = "https://logrr.internal"
    Private Const ApiKey As String = "lg_sample_xxxxxxxxxxxxxxxxxxxxxx"

    Sub Main()
        DirectClientExample()
        LoggingFrameworkExample()
        TemplateVsInterpolationExample()
        Console.WriteLine("Done.")
    End Sub

    ''' <summary>
    ''' No logging framework. Create ONE client for the life of the process and share it -
    ''' it batches internally, so a client per call would defeat the batching and leak a
    ''' timer and an HttpClient every time.
    ''' </summary>
    Private Sub DirectClientExample()
        Using client As New LogrrClient(New LogrrClientOptions With {
                .Endpoint = Endpoint,
                .ApiKey = ApiKey,
                .MinimumLevel = LogrrLevel.Information,
                .BatchSizeLimit = 500,
                .FlushInterval = TimeSpan.FromSeconds(2)
            })

            ' Named arguments let you skip `exception` without passing Nothing positionally.
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

            ' Emit gives full control - source, trace ids, an explicit timestamp.
            client.Emit(New LogrrEvent With {
                .Timestamp = DateTimeOffset.UtcNow,
                .Level = LogrrLevel.Information,
                .MessageTemplate = "Nightly reconciliation finished in {ElapsedMs} ms",
                .Source = "Billing.Reconciliation",
                .Properties = New Dictionary(Of String, Object) From {
                    {"ElapsedMs", 1843}
                }
            })

            ' VB has no async Main, so block explicitly when a flush must complete before
            ' the next statement. Disposing the client also does a bounded final flush.
            client.FlushAsync().GetAwaiter().GetResult()

            Console.WriteLine("Direct client dropped so far: " & client.DroppedCount.ToString())
        End Using
    End Sub

    ''' <summary>
    ''' Microsoft.Extensions.Logging. In a Generic Host or ASP.NET Core app this is
    ''' `builder.Logging.AddLogrr(...)`; LoggerFactory.Create is the equivalent for a plain
    ''' console or service that has no host.
    ''' </summary>
    Private Sub LoggingFrameworkExample()
        Using factory As ILoggerFactory = LoggerFactory.Create(
            Sub(logging)
                logging.SetMinimumLevel(LogLevel.Information)
                logging.AddLogrr(Endpoint, ApiKey)
            End Sub)

            Dim logger As ILogger = factory.CreateLogger("Billing")

            ' The category ("Billing") arrives as SourceContext.
            logger.LogInformation("Invoice {InvoiceId} issued to {Customer}", 90210, "Acme Ltd")

            Try
                ProcessPayment(49.99D, 1042)
            Catch ex As Exception
                logger.LogError(ex, "Payment {Amount} failed for {UserId}", 49.99D, 1042)
            End Try

            ' Scopes are accepted but NOT captured - BeginScope returns a no-op. Anything you
            ' need to search on must be a property of the event itself, so put correlation
            ' ids in the template rather than a surrounding scope.
            logger.LogWarning("Retry {Attempt} of {Max} for {CorrelationId}",
                              2, 3, "8f3c1e02-4b77-4b1e-9a55-2d0f6c1a77bd")
        End Using
    End Sub

    ''' <summary>
    ''' The single most common way to lose the value of structured logging from VB.
    ''' </summary>
    Private Sub TemplateVsInterpolationExample()
        Using client As New LogrrClient(New LogrrClientOptions With {
                .Endpoint = Endpoint,
                .ApiKey = ApiKey
            })

            Dim amount = 49.99D
            Dim userId = 1042

            ' WRONG: interpolation renders the values into the text before Logrr sees them.
            ' Every event gets a unique message, so they never group, and you cannot filter
            ' on Amount or UserId because no such properties exist.
            client.Log(LogrrLevel.Information, $"Payment {amount} failed for {userId}")

            ' RIGHT: the template stays constant and the values arrive as typed properties,
            ' so `UserId = 1042` is a searchable filter and the events group as one.
            client.Log(LogrrLevel.Information, "Payment {Amount} failed for {UserId}",
                       properties:=New Dictionary(Of String, Object) From {
                           {"Amount", amount},
                           {"UserId", userId}
                       })
        End Using
    End Sub

    Private Sub ProcessPayment(amount As Decimal, userId As Integer)
        Throw New InvalidOperationException(
            "Gateway declined " & amount.ToString() & " for user " & userId.ToString() & ".")
    End Sub

End Module
