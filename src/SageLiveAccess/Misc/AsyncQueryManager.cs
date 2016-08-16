﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Netco.Extensions;
using Netco.Monads;
using SageLiveAccess.Models.Auth;
using SageLiveAccess.sforce;
using Task = System.Threading.Tasks.Task;

namespace SageLiveAccess.Misc
{
    internal class AsyncQueryManager: MethodLogging
    {
        public const int MaxRetries = 20;
        public Func< int, Task > delay = retryNum => Task.Delay( ( 1 + retryNum ) * 5000 );

        private readonly SemaphoreSlim _monitor = new SemaphoreSlim( 0, 1 );
        private readonly SforceService _binding;
        private readonly string _refreshToken;
        private readonly SageLiveReAuthService _reAuthService;
        private readonly Guid _instanceId = Guid.NewGuid();
        private QueryResult qr;

        private const string ServiceName = "AsyncQueryManager";

        internal class TaskResult< T >
        {
            public readonly bool _success;
            public readonly Exception _exception;
            public readonly T _result;

            public TaskResult( bool success, Exception ex, T result )
            {
                this._success = success;
                this._exception = ex;
                this._result = result;
            }
        }

        internal class TaskIdentifier< T >
        {
            public readonly Guid _taskGUID;
            public readonly SemaphoreSlim _monitor;
            public TaskResult< T > result;

            public TaskIdentifier( AsyncQueryManager manager, SemaphoreSlim monitor )
            {
                this._taskGUID = manager._instanceId;
                this._monitor = monitor;
            }
        }

        public AsyncQueryManager( SforceService binding, SageLiveFactoryConfig config, string refreshToken )
        {
            this._binding = binding;
            this._binding.queryCompleted += this.Binding_queryCompleted;
            this._binding.queryMoreCompleted += this.Binding_queryMoreCompleted;
            this._binding.createCompleted += this.BindingOnCreateCompleted;
            this._binding.updateCompleted += this.BindingOnUpdateCompleted;
            this._binding.deleteCompleted += this.BindingOnDeleteCompleted;

            this._reAuthService = new SageLiveReAuthService( config );
            this._refreshToken = refreshToken;
        }

        private async Task< bool > RefreshTokenIfNeeded< T >( TaskResult< T > t )
        {
            if( !( t._exception is System.Web.Services.Protocols.SoapException ) )
                return false;
            var soapException = ( System.Web.Services.Protocols.SoapException )t._exception;
            if( soapException.Code.Name != "INVALID_SESSION_ID" )
                return false;

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Session token expired. Refreshing..." );

            var newSessionId = await this._reAuthService.GetRefreshedToken( this._refreshToken );
            this._binding.SessionHeaderValue.sessionId = newSessionId;
            return true;
        }

        public async Task< SaveResult[] > Insert( sObject[] objects )
        {
            return await this.RetryWrapper< SaveResult[] >( t =>
            {
                this._binding.createAsync( objects, t );
            }, "Inserting to SOQL: {0}".FormatWith( objects.MakeString() ) );
        }

        public async Task< SaveResult[] > Update( sObject[] objects )
        {
            return await this.RetryWrapper< SaveResult[] >( t =>
            {
                this._binding.updateAsync( objects, t );
            }, "Updating in SOQL: {0}".FormatWith( objects.MakeString() ) );
        }

        public async Task Delete( string[] ids )
        {
            await this.RetryWrapper< DeleteResult[] >( t =>
            {
                this._binding.deleteAsync( ids, t );
            }, "Deleting from SOQL: {0}".FormatWith( ids.MakeString() ) );
        }

        public async Task< T > RetryWrapper< T >( Action< TaskIdentifier< T > > f, string info )
        {
            int retryCount = 0;
            while( retryCount < 20 )
            {
                SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Trying {0} with Salesforce SOAP API for the {1} time...".FormatWith( info, retryCount ) );
                var indentifier = new TaskIdentifier< T >( this, new SemaphoreSlim( 0, 1 ) );
                f.Invoke( indentifier );
                await indentifier._monitor.WaitAsync();
                if( indentifier.result._success )
                {
                    SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Salesforce SOAP API request {0} succeeded".FormatWith( info ) );
                    return indentifier.result._result;
                }

                SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Salesforce SOAP API request {0} failed. Reason: {1} -> {2}. Retrying...".FormatWith( info, indentifier.result._exception.Message, indentifier.result._exception.StackTrace ) );
                if( !await this.RefreshTokenIfNeeded( indentifier.result ) )
                { 
                     await this.delay( retryCount );
                }
                retryCount++;
                if( retryCount >= 20 )
                    throw new AggregateException( "Maximum number of retries reached for SOAP API request {0}".FormatWith( info ), new List< Exception > { indentifier.result._exception } );
            }
            throw new InvalidOperationException( "You are not supposed to be here!" );
        }

        public async Task< QueryResult > QueryAsync( string soqlQuery )
        {
            //SemaphoreSlim taskMonitor = new SemaphoreSlim( 0, 1 );
            //this._binding.queryAsync( soqlQuery, new TaskIdentifier( this, taskMonitor ) );
            //await taskMonitor.WaitAsync();
            //return this.qr;
            return await this.RetryWrapper< QueryResult >( t =>
            {
                this._binding.queryAsync( soqlQuery, t );
            }, "Quering from SOQL: {0}".FormatWith( soqlQuery ) );
        }

        public async Task< Maybe< T > > QueryOneAsync< T >( SoqlQueryBuilder builder ) where T : class
        {
	        var query = builder.Build();
			var result = await this.RetryWrapper< QueryResult >( t =>
            {
                this._binding.queryAsync( query, t );
            }, "Quering single record from SOQL: {0}".FormatWith( query ) );
            if( result.records == null )
                return Maybe< T >.Empty;
            return result.records.Length > 0 ? result.records[ 0 ] as T : Maybe< T >.Empty;
        }

        public async Task< QueryResult > QueryMoreAsync( string query )
        {
			return await this.RetryWrapper< QueryResult >( t =>
            {
                this._binding.queryMoreAsync( query, t );
            }, "Quering more from SOQL: {0}".FormatWith( query ) );
        }

        void Binding_queryCompleted( object sender, queryCompletedEventArgs e )
        {
            if( !( e.UserState is TaskIdentifier< QueryResult > ) || !( ( TaskIdentifier< QueryResult > )e.UserState )._taskGUID.Equals( this._instanceId ) )
                return;

            var taskIdentifier = ( TaskIdentifier< QueryResult > )e.UserState;
            var result = new TaskResult< QueryResult >(
                e.Error == null,
                e.Error,
                e.Error == null ? e.Result : null
                );

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Query completed callback executing. Success = {0}".FormatWith( e.Error == null ) );
            taskIdentifier.result = result;
            taskIdentifier._monitor.Release();
        }

        void Binding_queryMoreCompleted( object sender, queryMoreCompletedEventArgs e )
        {
            if( !( e.UserState is TaskIdentifier< QueryResult > ) || !( ( TaskIdentifier< QueryResult > )e.UserState )._taskGUID.Equals( this._instanceId ) )
                return;

            var taskIdentifier = ( TaskIdentifier< QueryResult > )e.UserState;
            var result = new TaskResult< QueryResult >(
                e.Error == null,
                e.Error,
                e.Error == null ? e.Result : null
                );

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "QueryMore completed callback executing. Success = {0}".FormatWith( e.Error == null ) );
            taskIdentifier.result = result;
            taskIdentifier._monitor.Release();
        }

        private void BindingOnCreateCompleted( object sender, createCompletedEventArgs e )
        {
            if( !( e.UserState is TaskIdentifier< SaveResult[] > ) || !( ( TaskIdentifier< SaveResult[] > )e.UserState )._taskGUID.Equals( this._instanceId ) )
                return;

            var taskIdentifier = ( TaskIdentifier< SaveResult[] > )e.UserState;
            var result = new TaskResult< SaveResult[] >(
                e.Error == null,
                e.Error,
                e.Error == null ? e.Result : null
                );

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Insert completed callback executing. Success = {0}".FormatWith( e.Error == null ) );
            taskIdentifier.result = result;
            taskIdentifier._monitor.Release();
        }

        private void BindingOnUpdateCompleted( object sender, updateCompletedEventArgs e )
        {
            if( !( e.UserState is TaskIdentifier< SaveResult[] > ) || !( ( TaskIdentifier< SaveResult[] > )e.UserState )._taskGUID.Equals( this._instanceId ) )
                return;

            var taskIdentifier = ( TaskIdentifier< SaveResult[] > )e.UserState;
            var result = new TaskResult< SaveResult[] >(
                e.Error == null,
                e.Error,
                e.Error == null ? e.Result : null
                );

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Update completed callback executing. Success = {0}".FormatWith( e.Error == null ) );
            taskIdentifier.result = result;
            taskIdentifier._monitor.Release();
        }

        private void BindingOnDeleteCompleted( object sender, deleteCompletedEventArgs e )
        {
            if( !( e.UserState is TaskIdentifier< DeleteResult[] > ) || !( ( TaskIdentifier< DeleteResult[] > )e.UserState )._taskGUID.Equals( this._instanceId ) )
                return;

            var taskIdentifier = ( TaskIdentifier< DeleteResult[] > )e.UserState;
            var result = new TaskResult< DeleteResult[] >(
                e.Error == null,
                e.Error,
                e.Error == null ? e.Result : null
                );

            SageLiveLogger.Debug( this.GetLogPrefix( null, ServiceName ), "Update completed callback executing. Success = {0}".FormatWith( e.Error == null ) );
            taskIdentifier.result = result;
            taskIdentifier._monitor.Release();
        }
    }
}