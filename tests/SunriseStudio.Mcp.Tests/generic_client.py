"""Drives Solforge's MCP server with the official MCP Python SDK, a client Solforge did not write.

Usage: python generic_client.py http <url> <token>
       python generic_client.py stdio <command> [args...]   (SOLFORGE_MCP_DISCOVERY passes through the environment)

Prints one JSON object the checks program reads: server name, tool names, whether every tool has an object schema
and a description, the content types and image MIME types of get_viewport_frame, its text, a list_ui_controls page and
whether ui_invoke came back as a tool error (the fixture declines approvals). Exit code 3 means the SDK is missing.
"""
import asyncio
import json
import os
import sys

try:
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    import mcp.client.streamable_http as streamable
except ImportError:
    print(json.dumps({"skipped": "pip install mcp"}))
    sys.exit(3)


def field(obj, *names):
    """SDK 1.x uses camelCase attributes, 2.x snake_case."""
    for name in names:
        if hasattr(obj, name):
            return getattr(obj, name)
    return None


def http_transport(url, token):
    headers = {"Authorization": "Bearer " + token}
    if hasattr(streamable, "streamable_http_client"):
        return streamable.streamable_http_client(url, http_client=streamable.create_mcp_http_client(headers=headers))
    return streamable.streamablehttp_client(url, headers=headers)


async def drive(transport):
    async with transport as streams:
        read, write = streams[0], streams[1]
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            tools = (await session.list_tools()).tools
            frame = await session.call_tool("get_viewport_frame", {})
            controls = await session.call_tool("list_ui_controls", {"query": "grid"})
            denied = await session.call_tool("ui_invoke", {"query": "Build"})
            return {
                "server": field(field(init, "serverInfo", "server_info"), "name"),
                "tools": [tool.name for tool in tools],
                "schemas": all((field(tool, "inputSchema", "input_schema") or {}).get("type") == "object" and bool(tool.description) for tool in tools),
                "frameKinds": [item.type for item in frame.content],
                "mimes": [field(item, "mimeType", "mime_type") for item in frame.content if item.type == "image"],
                "frameText": next((item.text for item in frame.content if item.type == "text"), ""),
                "controls": next((item.text for item in controls.content if item.type == "text"), ""),
                "deniedError": bool(field(denied, "isError", "is_error")),
            }


def main():
    mode = sys.argv[1]
    if mode == "http":
        transport = http_transport(sys.argv[2], sys.argv[3])
    else:
        transport = stdio_client(StdioServerParameters(command=sys.argv[2], args=sys.argv[3:], env=dict(os.environ)))
    print(json.dumps(asyncio.run(drive(transport))))


if __name__ == "__main__":
    main()
