using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Voxelis.Rendering;
using UnityEngine.Networking;
using System.Net;
using System.Threading;

using NumSharp;

namespace Voxelis
{
    [RequireComponent(typeof(BlockGroup))]
    class HTTPWorldMod : MonoBehaviour
    {
		private HttpListener listener;
		private Thread listenerThread;

		private BlockGroup blockGroup;

		void Initialize()
		{
			blockGroup = GetComponent<BlockGroup>();

			listener = new HttpListener();
			listener.Prefixes.Add("http://localhost:4444/");
			listener.Prefixes.Add("http://127.0.0.1:4444/");
			listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
			listener.Start();

			listenerThread = new Thread(startListener);
			listenerThread.Start();
			Debug.Log("Server Started");
		}

		private bool _init = false;
		void Update()
		{
			if (!_init)
			{
				_init = true;
				Initialize();
			}
		}

        private void OnDisable()
        {
			listener.Stop();
			listenerThread.Abort();
        }

        private void startListener()
		{
			while (true)
			{
				var result = listener.BeginGetContext(ListenerCallback, listener);
				result.AsyncWaitHandle.WaitOne();
			}
		}

		private void ListenerCallback(IAsyncResult result)
		{
			var context = listener.EndGetContext(result);
			try
			{
				//Debug.Log("Method: " + context.Request.HttpMethod);
				//Debug.Log("LocalUrl: " + context.Request.Url.LocalPath);

				Dictionary<string, string> req = new Dictionary<string, string>();

				if (context.Request.QueryString.AllKeys.Length > 0)
					foreach (var key in context.Request.QueryString.AllKeys)
					{
						Debug.Log("Key: " + key + ", Value: " + context.Request.QueryString.GetValues(key)[0]);

						req.Add(key, context.Request.QueryString.GetValues(key)[0]);
					}

				// Get a block
				if (context.Request.HttpMethod == "GET")
				{
					switch (context.Request.Url.LocalPath)
					{
						case "/numpy":
							try
							{
								var strmin = req["min"].Split(',');
								var strmax = req["max"].Split(',');
								Vector3Int minVec = new Vector3Int(int.Parse(strmin[0]), int.Parse(strmin[1]), int.Parse(strmin[2]));
								Vector3Int maxVec = new Vector3Int(int.Parse(strmax[0]), int.Parse(strmax[1]), int.Parse(strmax[2]));
								BoundsInt range = new BoundsInt(minVec, maxVec - minVec);

								if((range.size.x * range.size.y * range.size.z) < (256 * 256 * 256))
                                {
									var mapdata = new NDArray(NPTypeCode.Int32, new int[] { range.size.x, range.size.y, range.size.z });

                                    foreach (var p in range.allPositionsWithin)
                                    {
										var block = blockGroup.GetBlock(p);

										var q = p - range.min;
										mapdata[q.x, q.y, q.z] = unchecked((int)block.id);
									}
									//byte[] bytes = mapdata.ToByteArray();
									np.Save((Array)mapdata, context.Response.OutputStream);
									//context.Response.OutputStream.Write(bytes, 0, bytes.Length);
								}
							}
							catch (KeyNotFoundException)
							{
								byte[] bytes = System.Text.Encoding.UTF8.GetBytes("Please give arguments: min?=x,y,z;max?=x,y,z");
								context.Response.OutputStream.Write(bytes, 0, bytes.Length);
							}
							break;
						default:
							try
							{
								var strpos = req["pos"].Split(',');
								Vector3Int position = new Vector3Int(int.Parse(strpos[0]), int.Parse(strpos[1]), int.Parse(strpos[2]));

								var block = blockGroup.GetBlock(position);
								byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"{block.id}");
								context.Response.OutputStream.Write(bytes, 0, bytes.Length);
							}
							catch (KeyNotFoundException)
							{
								byte[] bytes = System.Text.Encoding.UTF8.GetBytes("Please give arguments: pos?=x,y,z");
								context.Response.OutputStream.Write(bytes, 0, bytes.Length);
							}
							break;
                }
				}

				// Set a block
				if (context.Request.HttpMethod == "POST")
				{
					switch (context.Request.Url.LocalPath)
					{
						case "/batched":
							try
							{
								var body = new StreamReader(context.Request.InputStream);

								string line;
                                while ((line = body.ReadLine()) != null)
                                {
									var strpos = line.Split(',');
									Vector3Int position = new Vector3Int(int.Parse(strpos[0]), int.Parse(strpos[1]), int.Parse(strpos[2]));
									var block = uint.Parse(strpos[3]);

									blockGroup.SetBlock(position, new Block { id = block });
								}
							}
							catch (KeyNotFoundException e)
							{
								byte[] bytes = System.Text.Encoding.UTF8.GetBytes("Please give arguments: pos?=x,y,z, blk?=block");
								context.Response.OutputStream.Write(bytes, 0, bytes.Length);
							}
							break;
						default:
							try
							{
								var strpos = req["pos"].Split(',');
								Vector3Int position = new Vector3Int(int.Parse(strpos[0]), int.Parse(strpos[1]), int.Parse(strpos[2]));

								var block = uint.Parse(req["blk"]);
								blockGroup.SetBlock(position, new Block { id = block });
							}
							catch (KeyNotFoundException e)
							{
								byte[] bytes = System.Text.Encoding.UTF8.GetBytes("Please give arguments: pos?=x,y,z, blk?=block");
								context.Response.OutputStream.Write(bytes, 0, bytes.Length);
							}
							break;
					}
				}
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogError(e);

				byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"Parse error: {e}");
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
			}

			context.Response.OutputStream.Flush();
			context.Response.Close();
		}
	}
}
