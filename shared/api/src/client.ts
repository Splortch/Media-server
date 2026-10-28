import type { components } from "./generated";
export type ProgressState = components["schemas"]["ProgressState"];
export type ServerInfo = components["schemas"]["ServerInfo"];
export type Media = components["schemas"]["MediaSummary"];
export type Detail = components["schemas"]["MediaDetails"];
export type SessionInfo = components["schemas"]["SessionInfo"];
export type Tracks = components["schemas"]["TrackCatalog"];
export interface Transport {
  request<T>(method: string, path: string, body?: unknown): Promise<T>;
  streamUrl(id: number): Promise<string>;
}
function media(value: unknown): asserts value is Media {
  const x = value as Media;
  if (
    !x ||
    !Number.isSafeInteger(x.id) ||
    x.id <= 0 ||
    typeof x.title !== "string" ||
    x.title.length > 1024 ||
    ![x.duration, x.size].every(
      (n) => typeof n === "number" && Number.isFinite(n) && n >= 0,
    ) ||
    ![x.videoCodec, x.audioCodec, x.container].every(
      (s) => typeof s === "string" && s.length <= 512,
    )
  )
    throw new Error("Invalid media response from server.");
}
export function serverInfo(value: unknown): ServerInfo {
  const x = value as ServerInfo;
  if (
    !x ||
    x.apiVersion !== 1 ||
    x.minimumClientApiVersion > 1 ||
    typeof x.name !== "string" ||
    x.name.length > 128 ||
    typeof x.serverVersion !== "string" ||
    typeof x.setupRequired !== "boolean"
  )
    throw new Error("Incompatible or invalid server information.");
  return x;
}
export class ClientApi {
  constructor(public readonly transport: Transport) {}
  async me() {
    const x = await this.transport.request<{ name: string; role: string }>(
      "GET",
      "/api/v1/auth/me",
    );
    if (
      typeof x?.name !== "string" ||
      typeof x?.role !== "string" ||
      x.name.length > 64 ||
      x.role.length > 64
    )
      throw new Error("Invalid account response.");
    return x;
  }
  async media(search = "", offset = 0) {
    const page = await this.transport.request<Media[]>(
      "GET",
      `/api/v1/media?limit=200&offset=${offset}&search=${encodeURIComponent(search.slice(0, 200))}`,
    );
    if (!Array.isArray(page) || page.length > 200)
      throw new Error("Invalid library response.");
    page.forEach(media);
    return page;
  }
  async detail(id: number) {
    const x = await this.transport.request<Detail>(
      "GET",
      `/api/v1/media/${id}`,
    );
    media({ ...x, libraryId: 0 });
    if (x.id !== id) throw new Error("Incorrect media response.");
    return x;
  }
  async progress(id: number) {
    const p = await this.transport.request<ProgressState>(
      "GET",
      `/api/v1/media/${id}/progress`,
    );
    if (
      p?.mediaItemId !== id ||
      !Number.isSafeInteger(p.revision) ||
      p.revision < 0 ||
      !Number.isFinite(p.seconds) ||
      p.seconds < 0 ||
      typeof p.completed !== "boolean"
    )
      throw new Error("Invalid progress response.");
    return p;
  }
  async continuing() {
    const p = await this.transport.request<ProgressState[]>(
      "GET",
      "/api/v1/continue-watching",
    );
    if (
      !Array.isArray(p) ||
      p.length > 100 ||
      p.some(
        (x) =>
          !x ||
          !Number.isSafeInteger(x.mediaItemId) ||
          x.mediaItemId <= 0 ||
          !Number.isFinite(x.seconds) ||
          x.seconds < 0 ||
          !Number.isSafeInteger(x.revision) ||
          x.revision < 0 ||
          typeof x.completed !== "boolean",
      )
    )
      throw new Error("Invalid Continue Watching response.");
    return p;
  }
  async tracks(id: number) {
    const result = await this.transport.request<Tracks>(
      "GET",
      `/api/v1/media/${id}/tracks`,
    );
    if (
      !result ||
      typeof result.canSelectAudio !== "boolean" ||
      typeof result.canSelectSubtitles !== "boolean" ||
      !Array.isArray(result.tracks) ||
      result.tracks.length > 256 ||
      result.tracks.some(
        (t) =>
          !t ||
          !Number.isSafeInteger(t.index) ||
          typeof t.type !== "string" ||
          typeof t.codec !== "string" ||
          (t.language !== null && typeof t.language !== "string"),
      )
    )
      throw new Error("Invalid track response.");
    return result;
  }
  async sessions() {
    const result = await this.transport.request<SessionInfo[]>(
      "GET",
      "/api/v1/sessions",
    );
    if (
      !Array.isArray(result) ||
      result.length > 1000 ||
      result.some(
        (s) =>
          !s ||
          typeof s.id !== "string" ||
          !/^([A-Fa-f0-9]{32})$/.test(s.id) ||
          typeof s.deviceName !== "string" ||
          s.deviceName.length > 64 ||
          typeof s.current !== "boolean" ||
          typeof s.expiresAt !== "string" ||
          !Number.isFinite(Date.parse(s.expiresAt)),
      )
    )
      throw new Error("Invalid session response.");
    return result;
  }
  revoke(id: string) {
    return this.transport.request("DELETE", `/api/v1/sessions/${id}`);
  }
  report(
    id: number,
    code: "network" | "decode" | "unsupported" | "unknown",
    client = "windows-webview2",
  ) {
    return this.transport.request(
      "POST",
      `/api/v1/media/${id}/playback-errors`,
      { code, client },
    );
  }
  capabilities(
    containers: string[],
    codecs: string[],
    identity = { platform: "windows", player: "WebView2" },
  ) {
    return this.transport.request("PUT", "/api/v1/capabilities", {
      platform: identity.platform,
      player: identity.player,
      containers,
      codecs,
    });
  }
}
// Serialize local events and stop on conflict/failure. Never replay stale offline positions.
export class ProgressWriter {
  private pending: Promise<void> = Promise.resolve();
  private blocked = false;
  constructor(
    private readonly api: ClientApi,
    private readonly id: number,
    private revision: number,
  ) {}
  save(seconds: number, completed = false): Promise<void> {
    const next = this.pending
      .catch(() => {})
      .then(async () => {
        if (this.blocked)
          throw new Error(
            "Progress could not be synchronized. Reopen this video to fetch the server position.",
          );
        try {
          const result = await this.api.transport.request<ProgressState>(
            "PUT",
            `/api/v1/media/${this.id}/progress-sync`,
            { seconds, completed, expectedRevision: this.revision },
          );
          if (!Number.isSafeInteger(result?.revision))
            throw new Error("Invalid progress acknowledgement.");
          this.revision = result.revision;
        } catch (e) {
          this.blocked = true;
          throw e;
        }
      });
    this.pending = next;
    return next;
  }
}
