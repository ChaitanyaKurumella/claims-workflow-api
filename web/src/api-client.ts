export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}
export class ApiClient {
  constructor(
    private readonly send: typeof fetch = (...args) =>
      globalThis.fetch(...args),
  ) {}
  async request<T>(
    path: string,
    key: string,
    body?: unknown,
    extra: Record<string, string> = {},
  ): Promise<T> {
    const r = await this.send(path, {
      method: body === undefined ? "GET" : "POST",
      headers: {
        ...(key
          ? key.startsWith("Bearer ")
            ? { Authorization: key }
            : { "X-Api-Key": key }
          : {}),
        "Content-Type": "application/json",
        ...extra,
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    if (!r.ok) {
      const e: unknown = await r.json().catch(() => null);
      const detail =
        e &&
        typeof e === "object" &&
        "detail" in e &&
        typeof e.detail === "string"
          ? e.detail
          : null;
      throw new ApiError(
        r.status,
        detail ||
          (
            {
              401: "Choose a demo role or connect with a configured API key.",
              403: "Your role cannot perform this action.",
            } as Record<number, string>
          )[r.status] ||
          "Request failed: " + r.status,
      );
    }
    return r.json() as Promise<T>;
  }
}
