(*** hide ***)
#r @"..\..\src\SqlClient\bin\Debug\FSharp.Data.SqlClient.dll"
#r "System.Transactions"
open FSharp.Data

[<Literal>]
let connectionString = @"Data Source=.;Initial Catalog=AdventureWorks2014;Integrated Security=True"
    
(**
Transactions 
===================

<div class="well well-small" style="margin:0px 70px 0px 20px;">

This chapter assumes a basic understanding of the "Transactions and Concurrency" topic as it applies to ADO.NET and SQL Server. 
(If you need to brush up your knowledge use favorite search engine \- there is plenty of information on the subject.)

The following links may be helpful:

 - Chapter 9 of [Microsoft SQL Server 2012 T-SQL Fundamentals](http://www.amazon.com/gp/product/0735658145). 
 - [Transactions and Concurrency in ADO.NET](https://msdn.microsoft.com/en-us/library/777e5ebh.aspx). 
 - Microsoft Virtual Academy has relevant courses
    - [Developing Microsoft SQL Server Databases. Chapter 04 - Managing Transactions.](http://www.microsoftvirtualacademy.com/training-courses/developing-microsoft-sql-server-databases) 
    - [Querying with Transact-SQL. Chapter 11 - Error Handling and Transactions.](http://www.microsoftvirtualacademy.com/training-courses/querying-with-transact-sql)

</p></div>

Explicit Transactions
-------------------------------------
Command types generated by both the SqlCommandProvider and the SqlProgrammabilityProvider have a constructor which accepts connection instance and optionally transaction information. 
This conforms to familiar [ADO.NET conventions](https://msdn.microsoft.com/en-us/library/352y4sff.aspx) for command constructors. 
*)

open System
open System.Data.SqlClient

type CurrencyCode = 
    SqlEnumProvider<"SELECT Name, CurrencyCode FROM Sales.Currency", connectionString>

type InsertCurrencyRate = SqlCommandProvider<"
        INSERT INTO Sales.CurrencyRate 
        VALUES (@currencyRateDate, @fromCurrencyCode, @toCurrencyCode, 
                @averageRate, @endOfDayRate, @modifiedDate) 
    ", connectionString>
do
    //Don't forget `use` binding to properly scope transactoin.
    //It guarantees rollback in case of unhandled exception. 

    use conn = new SqlConnection(connectionString)
    conn.Open()
    use tran = conn.BeginTransaction()
    
    //Implicit assumption that
    assert (tran.Connection = conn)
    //Supply connection and transaction 
    use cmd = new InsertCurrencyRate(conn, tran)

    let today = DateTime.Now.Date

    let recordsInserted = 
        cmd.Execute(
            currencyRateDate = today, 
            fromCurrencyCode = CurrencyCode.``US Dollar``, 
            toCurrencyCode = CurrencyCode.``United Kingdom Pound``, 
            averageRate = 0.63219M, 
            endOfDayRate = 0.63219M, 
            modifiedDate = today) 

    assert (recordsInserted = 1)

    // Invoke Commit otherwise transaction will be disposed (roll-backed) when out of scope
    tran.Commit()

(**
Note, that ``Connection`` property of the transaction instance has to match the connection supplied up at the first position. 

Most often transactions are used in combination with data modification commands (INSERT, UPDATE, DELETE, MERGE). 
Commands based on SELECT statements or calls to a stored procedure (function) can join a transaction as well, but generally
do not do so if they stand alone.

*)

type AdventureWorks = SqlProgrammabilityProvider<connectionString>

do
    use conn = new SqlConnection(connectionString)
    conn.Open()
    //bump up isolation level to serializable
    use tran = conn.BeginTransaction(Data.IsolationLevel.Serializable)
    let jamesKramerId = 42

    let businessEntityID, jobTitle, hireDate = 
        //Include SELECT in transaction
        //Note that inline definition requires both design time connection string
        // and runtime connection object
        use cmd = new SqlCommandProvider<"
            SELECT 
	            BusinessEntityID
	            ,JobTitle
	            ,HireDate
            FROM 
                HumanResources.Employee 
            WHERE 
                BusinessEntityID = @id
            ", connectionString, ResultType.Tuples, SingleRow = true>(conn, tran)

        jamesKramerId |> cmd.Execute |> Option.get

    assert("Production Technician - WC60" = jobTitle)
    
    let newJobTitle = "Uber " + jobTitle

    use updatedJobTitle = new AdventureWorks.HumanResources.uspUpdateEmployeeHireInfo(conn, tran)
    let recordsAffrected = 
        updatedJobTitle.Execute(
            businessEntityID, 
            newJobTitle, 
            hireDate, 
            RateChangeDate = DateTime.Now, 
            Rate = 12M, 
            PayFrequency = 1uy, 
            CurrentFlag = true 
        )
    
    let updatedJobTitle = 
        // Static Create factory method can also be used to pass connection and/or transaction
        // It provides better intellisense. See a link below
        // https://github.com/Microsoft/visualfsharp/issues/449
        use cmd = AdventureWorks.dbo.ufnGetContactInformation.Create(conn, tran)
        //Use ExecuteSingle if you're sure it return 0 or 1 rows
        let result = cmd.ExecuteSingle(PersonID = jamesKramerId) 
        result.Value.JobTitle.Value

    assert(newJobTitle = updatedJobTitle)

    tran.Commit()

(**
Implicit a.k.a Ambient Transactions
-------------------------------------

It can become tedious to pass around a connection or transaction pair over and over again. 
Fortunately, the .NET BCL class [TransactionScope](https://msdn.microsoft.com/en-us/library/system.transactions.transactionscope.aspx) was designed to address such tediousness. The basic idea is that all database connections opened within specific scope are included in that transaction. Thus, the example above can be re-written as follows:
*)

open System.Transactions
do
    use tran = new TransactionScope()

    let jamesKramerId = 42

    let businessEntityID, jobTitle, hireDate = 
        use cmd = new SqlCommandProvider<"
            SELECT 
	            BusinessEntityID
	            ,JobTitle
	            ,HireDate
            FROM 
                HumanResources.Employee 
            WHERE 
                BusinessEntityID = @id
            ", connectionString, ResultType.Tuples, SingleRow = true>()

        jamesKramerId |> cmd.Execute |> Option.get

    assert("Production Technician - WC60" = jobTitle)
    
    let newJobTitle = "Uber " + jobTitle

    use updatedJobTitle = new AdventureWorks.HumanResources.uspUpdateEmployeeHireInfo()
    let recordsAffrected = 
        updatedJobTitle.Execute(
            businessEntityID, 
            newJobTitle, 
            hireDate, 
            RateChangeDate = DateTime.Now, 
            Rate = 12M, 
            PayFrequency = 1uy, 
            CurrentFlag = true 
        )
    
    let updatedJobTitle = 
        use cmd = new AdventureWorks.dbo.ufnGetContactInformation()
        //Use ExecuteSingle on sproc/function generated types
        //if you're sure it return 0 or 1 rows
        let result = cmd.ExecuteSingle(PersonID = jamesKramerId) 
        result.Value.JobTitle.Value

    assert(newJobTitle = updatedJobTitle)

    tran.Complete()

(**
Although very convenient, `TransactionScope` has some pitfalls and 
therefore requires a good understanding of what happens behind the scenes. 
Make sure to read  the [General Usage Guidelines](https://msdn.microsoft.com/en-us/library/ee818746.aspx) to avoid common mistakes.

There are two kind of issues you might run into when using `TransactionScope`:

Unexpectedly Distributed Transactions 
-------------------------------------
[Distributed Transactions](https://msdn.microsoft.com/en-us/library/ms254973.aspx) spell all kind of trouble. 
They are rarely required and should be avoided in most cases. 
Strictly speaking this problem is not specific to `TransactionScope`, but it can be exacerbated by 
automatic [Transaction Management Escalation](https://msdn.microsoft.com/en-us/library/ee818742.aspx), which thus makes it annoying easy to fall into the trap.

If a local transaction was accidently promoted to distributed it should be considered a design problem. It's generally best to have a simple check in your code right before `commit` to reveal the issue: 

*)

do
    use tran = new TransactionScope()
    //your transaction logic here
    let isDistributed = Transaction.Current.TransactionInformation.DistributedIdentifier <> Guid.Empty
    if isDistributed 
    then invalidOp "Unexpected distributed transaction."
    else tran.Complete()


(**

<div class="well well-small" style="margin:0px 70px 0px 20px;">

**TIP** SQL Server can use multiple `SQLConnections` in a `TransactionScope` without escalating, 
provided the connections are not open at the same time (which would result in multiple "physical" TCP connections and thus require escalation).
</p></div>

<div class="well well-small" style="margin:0px 70px 0px 20px;">

**TIP** The value of the [Enlist](https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlconnectionstringbuilder.enlist.aspx) 
key from [SqlConnection.ConnectionString](https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlconnection.connectionstring.aspx) 
property determines the auto-enlistment behavior of connection instance.
</p></div>

TransactionScope + AsyncExecute 
-------------------------------------

Another tricky problem involves combining `TransactionScope` with asynchronous execution. 
`TransactoinScope` [has thread affinity](http://stackoverflow.com/q/13543254/1603572). 
To propagate the transaction context to another thread, .NET 4.5.1 introduced [TransactionScopeAsyncFlowOption.Enabled](https://msdn.microsoft.com/en-us/library/system.transactions.transactionscopeasyncflowoption.aspx) which needs to be passed into the [TransactionScope constructor](https://msdn.microsoft.com/en-us/library/dn261473.aspx). 
Unfortunately if you are stuck with .NET Framework prior to version 4.5.1 the only way 
to combine `TransactionScope` with `AsyncExecute` is explicit transactions.  

*)

do
    use tran = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled)
    
    use cmd = new SqlCommandProvider<"
        INSERT INTO Sales.CurrencyRate 
        VALUES (@currencyRateDate, @fromCurrencyCode, @toCurrencyCode, 
                @averageRate, @endOfDayRate, @modifiedDate) 
    ", connectionString>()

    let today = DateTime.Now.Date

    let recordsInserted = 
        cmd.AsyncExecute(
            currencyRateDate = today, 
            fromCurrencyCode = CurrencyCode.``US Dollar``, 
            toCurrencyCode = CurrencyCode.``United Kingdom Pound``, 
            averageRate = 0.63219M, 
            endOfDayRate = 0.63219M, 
            modifiedDate = today) 
        |> Async.RunSynchronously

    assert (recordsInserted = 1)

    tran.Complete()

DataTable Updates/Bulk Load
-------------------------------------
Statically typed data tables generated either by SqlProgrammabilityProvider or by SqlCommandProvider 
with ResultType.DataTable have two helper methods `Update` and `BulkCopy` to send changes back into the database. 
Both methods accept connection and transaction to support explicit transactions.

The example above which inserts a new USD/GBP currency rate can be re-written as
*)

do
    let currencyRates = new AdventureWorks.Sales.Tables.CurrencyRate()
    let today = DateTime.Now
    currencyRates.AddRow(
            CurrencyRateDate = today, 
            FromCurrencyCode = CurrencyCode.``US Dollar``, 
            ToCurrencyCode = CurrencyCode.``United Kingdom Pound``, 
            AverageRate = 0.63219M, 
            EndOfDayRate = 0.63219M)

    use conn = new SqlConnection(connectionString)
    conn.Open()
    use tran = conn.BeginTransaction()

    let recordsAffected = currencyRates.Update(conn, tran)
    assert (recordsAffected = 1)
    
    //or Bulk Load
    //currencyRates.BulkCopy(conn, transaction = tran)

    tran.Commit()

(**
Same as above with implicit transaction.
*)

do
    let currencyRates = new AdventureWorks.Sales.Tables.CurrencyRate()
    let today = DateTime.Now
    currencyRates.AddRow(
            CurrencyRateDate = today, 
            FromCurrencyCode = CurrencyCode.``US Dollar``, 
            ToCurrencyCode = CurrencyCode.``United Kingdom Pound``, 
            AverageRate = 0.63219M, 
            EndOfDayRate = 0.63219M)

    use tran = new TransactionScope()

    let recordsAffected = currencyRates.Update()
    assert (recordsAffected = 1)
    
    //or Bulk Load
    //currencyRates.BulkCopy()

    tran.Complete()

(**
    
*)
