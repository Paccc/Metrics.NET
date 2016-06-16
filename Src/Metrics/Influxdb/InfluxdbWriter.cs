﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Metrics.Logging;

namespace Metrics.Influxdb
{
	/// <summary>
	/// The <see cref="InfluxdbWriter"/> is responsible for writing <see cref="InfluxRecord"/>s to the InfluxDB server.
	/// Derived classes can implement various methods and protocols for writing the data (ie. HTTP API, UDP, etc).
	/// </summary>
	public abstract class InfluxdbWriter : IDisposable
	{
		// formats bytes into a string with units either in bytes or KiB
		protected static readonly Func<Int64, String> fmtSize = bytes => bytes < (1 << 12) ? $"{bytes:n0} bytes" : $"{bytes / 1024.0:n2} KiB";

		protected readonly InfluxBatch batch;
		protected Int32 batchSize;


		#region Public Data Members

		/// <summary>
		/// The currently buffered <see cref="InfluxBatch"/> that has not yet been flushed to the underlying writer.
		/// </summary>
		public InfluxBatch Batch {
			get { return batch; }
		}

		/// <summary>
		/// The maximum number of records to write per flush. Set to zero to write all records in a single flush. Negative numbers are not allowed.
		/// </summary>
		public Int32 BatchSize {
			get { return batchSize; }
			set {
				if (value < 0)
					throw new ArgumentOutOfRangeException(nameof(value), "Batch size cannot be negative.");
				batchSize = value;
			}
		}

		#endregion


		/// <summary>
		/// Creates a new instance of a <see cref="InfluxdbWriter"/>.
		/// </summary>
		public InfluxdbWriter() 
			: this(0) {
		}

		/// <summary>
		/// Creates a new instance of a <see cref="InfluxdbWriter"/>.
		/// </summary>
		/// <param name="batchSize">The maximum number of records to write per flush. Set to zero to write all records in a single flush. Negative numbers are not allowed.</param>
		public InfluxdbWriter(Int32 batchSize) {
			if (batchSize < 0)
				throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size cannot be negative.");

			this.batch = new InfluxBatch();
			this.batchSize = batchSize;
		}


		#region Public Methods

		/// <summary>
		/// Flushes all buffered records in the batch by writing them to the server in a single write operation.
		/// </summary>
		public abstract void Flush();

		/// <summary>
		/// Writes the record to the InfluxDB server. If batching is used, the record will be added to the
		/// batch buffer but will not immediately be written to the server. If the number of buffered records
		/// is greater than or equal to the BatchSize, then the batch will be flushed to the underlying writer.
		/// </summary>
		/// <param name="record">The record to write.</param>
		public virtual void Write(InfluxRecord record) {
			if (record == null) throw new ArgumentNullException(nameof(record));
			batch.Add(record);
			if (batchSize > 0 && batch.Count >= batchSize)
				Flush(); // flush if batch is full
		}

		/// <summary>
		/// Writes the records to the InfluxDB server. Flushing will occur in increments of the defined BatchSize.
		/// </summary>
		/// <param name="records">The records to write.</param>
		public virtual void Write(IEnumerable<InfluxRecord> records) {
			if (records == null) throw new ArgumentNullException(nameof(records));
			foreach (var r in records)
				Write(r);
		}

		/// <summary>
		/// Flushes all buffered records and clears the batch.
		/// </summary>
		public virtual void Dispose() {
			try {
				Flush();
			} finally {
				batch.Clear();
			}
		}

		#endregion

	}

	/// <summary>
	/// This class writes <see cref="InfluxRecord"/>s formatted in the LineProtocol to the InfluxDB server.
	/// </summary>
	public abstract class InfluxdbLineWriter : InfluxdbWriter
	{

		private static readonly ILog log = LogProvider.GetCurrentClassLogger();


		/// <summary>
		/// Creates a new <see cref="InfluxdbLineWriter"/>.
		/// </summary>
		public InfluxdbLineWriter()
			: base() {
		}


		/// <summary>
		/// Flushes all buffered records in the batch by writing them to the server.
		/// </summary>
		public override void Flush() {
			Byte[] bytes = new Byte[0];
			String strBatch = String.Empty;
			try {
				if (Batch.Count == 0) return;
				strBatch = Batch.ToLineProtocol();
				bytes = Encoding.UTF8.GetBytes(strBatch);
				WriteToTransport(bytes);
			} catch (Exception ex) {
				String firstNLines = String.Join("\n", strBatch.Split('\n').Take(5));
				MetricsErrorHandler.Handle(ex, $"Error while flushing {Batch.Count} measurements to InfluxDB. Bytes: {fmtSize(bytes.Length)} - First 5 lines: {firstNLines}");
			} finally {
				// clear always, regardless if it was successful or not
				Batch.Clear();
			}
		}

		/// <summary>
		/// Writes the byte array to the InfluxDB server using the underlying transport.
		/// </summary>
		/// <param name="bytes">The bytes to write to the InfluxDB server.</param>
		/// <returns>The response from the server after writing the message, or null if there is no response (like for UDP).</returns>
		protected abstract Byte[] WriteToTransport(Byte[] bytes);

	}


	/// <summary>
	/// This class writes <see cref="InfluxRecord"/>s formatted in the LineProtocol to the InfluxDB server using HTTP POST.
	/// </summary>
	public class InfluxdbHttpWriter : InfluxdbLineWriter
	{

		protected readonly Uri influxDbUri;


		/// <summary>
		/// Creates a new <see cref="InfluxdbHttpWriter"/> with the specified URI.
		/// </summary>
		/// <param name="influxDbUri">The HTTP URI of the InfluxDB server.</param>
		public InfluxdbHttpWriter(Uri influxDbUri)
			: base() {
			if (influxDbUri == null)
				throw new ArgumentNullException(nameof(influxDbUri));
			if (influxDbUri.Scheme != Uri.UriSchemeHttp && influxDbUri.Scheme != Uri.UriSchemeHttps)
				throw new ArgumentException($"The URI scheme must be either http or https. Scheme: {influxDbUri.Scheme}", nameof(influxDbUri));

			this.influxDbUri = influxDbUri;
		}


		/// <summary>
		/// Writes the byte array to the InfluxDB server in a single HTTP POST operation.
		/// </summary>
		/// <param name="bytes">The bytes to write to the InfluxDB server.</param>
		/// <returns>The HTTP response from the server after writing the message.</returns>
		protected override Byte[] WriteToTransport(Byte[] bytes) {
			try {
				using (var client = new WebClient()) {
					var result = client.UploadData(influxDbUri, bytes);
					return result;
				}
			} catch (WebException ex) {
				String response = new StreamReader(ex.Response.GetResponseStream()).ReadToEnd();
				MetricsErrorHandler.Handle(ex, $"Error while uploading {Batch.Count} measurements to InfluxDB over HTTP [{influxDbUri}] [ResponseStatus: {ex.Status}]: {response}");
				return Encoding.UTF8.GetBytes(response);
			}
		}
	}
}
