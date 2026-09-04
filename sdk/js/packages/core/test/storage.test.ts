import { describe, expect, it } from "vitest";
import { Praxy } from "../src/client";
import { PraxyAuthError } from "../src/errors";
import type { TransportRequest, TransportResponse } from "../src/transport";
import { bytesResponse, emptyResponse, FakeTransport, jsonResponse } from "./support/fake-transport";

function clientCapturing(response: TransportResponse) {
  let captured!: TransportRequest;
  const transport = new FakeTransport((req) => {
    captured = req;
    return response;
  });
  const client = new Praxy({ endpoint: "https://api.test", projectId: "proj_1", transport });
  return { client, captured: () => captured };
}

const file = {
  id: "f1",
  bucketId: "b1",
  name: "avatar.png",
  mimeType: "image/png",
  sizeBytes: 3,
  chunkSizeBytes: 524_288,
  chunkCount: 1,
  checksum: "abc123",
  createdAt: "t",
  updatedAt: "t",
  $permissions: [],
};

describe("StorageService", () => {
  it("createFile() sends the raw bytes as the body, with the name in the query", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, file));
    const bytes = new Uint8Array([1, 2, 3]);

    const created = await client.storage.createFile("b1", { name: "avatar.png", bytes, mimeType: "image/png" });

    expect(captured().method).toBe("POST");
    expect(captured().path).toBe("/v1/storage/buckets/b1/files");
    expect(captured().query).toEqual({ name: ["avatar.png"] });
    // The bytes go verbatim — never JSON-encoded, which would corrupt them.
    expect(captured().bodyBytes).toBe(bytes);
    expect(captured().body).toBeUndefined();
    expect(captured().contentType).toBe("image/png");
    expect(created.checksum).toBe("abc123");
  });

  it("createFile() sends per-file grants in the query, since the body is the bytes", async () => {
    const permissions = ['read("user:u1")', 'delete("user:u1")'];
    const { client, captured } = clientCapturing(jsonResponse(201, { ...file, $permissions: permissions }));

    const created = await client.storage.createFile("b1", {
      name: "avatar.png",
      bytes: new Uint8Array([1]),
      permissions,
    });

    expect(captured().query).toEqual({ name: ["avatar.png"], permissions });
    expect(created.$permissions).toEqual(permissions);
  });

  it("createFile() omits the permissions key entirely when none are given", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, file));
    await client.storage.createFile("b1", { name: "a.png", bytes: new Uint8Array([1]), permissions: [] });
    expect(captured().query).toEqual({ name: ["a.png"] });
  });

  it("createFile() without a mime type leaves the content type to the transport default", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, file));
    await client.storage.createFile("b1", { name: "blob.bin", bytes: new Uint8Array([9]) });
    expect(captured().contentType).toBeUndefined();
  });

  it("createFile() of an empty file still sends an (empty) byte body", async () => {
    const { client, captured } = clientCapturing(jsonResponse(201, { ...file, sizeBytes: 0, chunkCount: 0 }));
    const created = await client.storage.createFile("b1", { name: "empty.txt", bytes: new Uint8Array() });
    expect(captured().bodyBytes).toEqual(new Uint8Array());
    expect(created.chunkCount).toBe(0);
  });

  it("listFiles() passes paging through and decodes the list", async () => {
    const { client, captured } = clientCapturing(
      jsonResponse(200, { total: 2, files: [file, { ...file, id: "f2", name: "b.png" }] }),
    );

    const list = await client.storage.listFiles("b1", { limit: 10, offset: 20 });

    expect(captured().path).toBe("/v1/storage/buckets/b1/files");
    expect(captured().query).toEqual({ limit: ["10"], offset: ["20"] });
    expect(list.files.map((f) => f.name)).toEqual(["avatar.png", "b.png"]);
  });

  it("listFiles() with no paging sends an empty query", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, { total: 0, files: [] }));
    await client.storage.listFiles("b1");
    expect(captured().query).toEqual({});
  });

  it("getFile() reads one file's metadata", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, file));
    const result = await client.storage.getFile("b1", "f1");
    expect(captured().method).toBe("GET");
    expect(captured().path).toBe("/v1/storage/buckets/b1/files/f1");
    expect(result.mimeType).toBe("image/png");
  });

  it("getFileDownload() asks for bytes and returns them untouched", async () => {
    // Bytes that are not valid UTF-8 and include a newline: nothing decoded them on the way through.
    const payload = new Uint8Array([0, 255, 10, 13]);
    const { client, captured } = clientCapturing(bytesResponse(200, payload));

    const bytes = await client.storage.getFileDownload("b1", "f1");

    expect(captured().path).toBe("/v1/storage/buckets/b1/files/f1/download");
    expect(captured().expect).toBe("bytes");
    expect(bytes).toEqual(payload);
  });

  it("getFileDownload() maps an error status the same way a JSON call does", async () => {
    const { client } = clientCapturing(
      jsonResponse(401, {
        message: "Not permitted to read files in this bucket.",
        code: 401,
        type: "general_unauthorized",
        version: "0.1.0",
        requestId: "r1",
      }),
    );

    await expect(client.storage.getFileDownload("b1", "f1")).rejects.toBeInstanceOf(PraxyAuthError);
  });

  it("deleteFile() issues a DELETE and tolerates the 204 empty body", async () => {
    const { client, captured } = clientCapturing(emptyResponse(204));
    await client.storage.deleteFile("b1", "f1");
    expect(captured().method).toBe("DELETE");
    expect(captured().path).toBe("/v1/storage/buckets/b1/files/f1");
  });

  it("ids are percent-encoded into the path", async () => {
    const { client, captured } = clientCapturing(jsonResponse(200, file));
    await client.storage.getFile("b 1", "f/1");
    expect(captured().path).toBe("/v1/storage/buckets/b%201/files/f%2F1");
  });
});
