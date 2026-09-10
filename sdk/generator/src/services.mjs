// @ts-check
/**
 * Every service this repo generates, and the per-target decisions behind each.
 *
 * Adding a **service** is an entry here. Adding a **language** is a file in `targets/` plus a
 * `targets:` key on each service. Neither is a new program — that is the whole point of one
 * generator rather than one per language.
 *
 * `modelTypes` is the load-bearing field. Each SDK already hand-writes the models that carry
 * design (`AppUser`, `AppSession`, …), and regenerating them would either duplicate those types or
 * quietly replace them with worse ones. So the generator is *told* which schemas already have an
 * equivalent, per language, and emits classes only for the ones that do not. A schema in neither
 * map is a hard error rather than a guessed type: a generator that invents types produces an SDK
 * whose bugs look like the server's.
 */

/** @type {any[]} */
export const services = [
  {
    namespace: "users",
    description: [
      "Server-side app-user administration (`/v1/users`) — the surface an API key reaches and an " +
        "end-user session never should. Create, inspect, update and delete a project's app users, " +
        "and revoke their sessions.",
      "Requires a client authenticated with an API key; every method here needs the " +
        "`users.read`/`users.write` key scopes.",
      "GENERATED from the OpenAPI document — see sdk/generator.",
    ],
    targets: {
      dart: {
        className: "UsersService",
        modelTypes: {
          AppUserResponse: "AppUser",
          SessionResponse: "AppSession",
          SessionListResponse: "SessionList",
        },
        generateModels: { AppUserListResponse: "AppUserList" },
      },
      csharp: {
        className: "UsersService",
        modelTypes: {
          AppUserResponse: "AppUser",
          SessionResponse: "AppSession",
          SessionListResponse: "SessionList",
        },
        generateModels: { AppUserListResponse: "AppUserList" },
      },
    },
  },
];
