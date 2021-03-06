﻿using System;
using System.Runtime.CompilerServices;
using Netco.Logging;

namespace SageLiveAccess.Misc
{
	internal class SageLiveLogger
	{
		private const string EmptyParamValue = "N/A";

		public static ILogger Log()
		{
			return NetcoLogger.GetLogger( "SageLiveLogger" );
		}

		public static void Debug( string prefix, string info )
		{
			Log().Debug( "[SageLive] {0}. {1}", prefix, info );
		}

		public static void Error( Exception ex, string prefix, string info )
		{
			Log().Error( ex, "[SageLive] {0}. {1}", prefix, info );
		}

		public static void LogStarted( Mark mark, string info = "", [ CallerMemberName ] string methodName = "" )
		{
			info = FillIfEmptyParam( info );
			Log().Trace( "[SageLive] Start call:{0}, Mark:{1}, Info:{2}", methodName, mark, info );
		}

		public static void LogEnd( Mark mark, string info = "", string result = "", [ CallerMemberName ] string methodName = "" )
		{
			info = FillIfEmptyParam( info );
			result = FillIfEmptyParam( result );
			Log().Trace( "[SageLive] End call:{0}, Mark:{1}, Info:{2}, Result:{3}", methodName, mark, info, result );
		}

		private static string FillIfEmptyParam( string info )
		{
			return !string.IsNullOrWhiteSpace( info ) ? info : EmptyParamValue;
		}
	}
}