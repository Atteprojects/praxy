/**
 * The org id is not on the session, so which organization you were last looking at is remembered
 * client-side, per browser. Its own module because two unrelated places need it: `HomeRedirect`
 * reads it to resolve "/", and `OrganizationLayout` writes it on every organization route.
 *
 * Every access is wrapped — private browsing and disabled site data both throw on the accessor
 * itself, not just return null, and switching organizations has to keep working when they do. It
 * simply won't be remembered.
 */
const LAST_ORGANIZATION_KEY = "praxy.lastOrganizationId";

export function rememberOrganization(organizationId: string) {
  try {
    localStorage.setItem(LAST_ORGANIZATION_KEY, organizationId);
  } catch {
    // Storage unavailable — switching still works, it just won't survive a reload.
  }
}

export function lastRememberedOrganization(): string | null {
  try {
    return localStorage.getItem(LAST_ORGANIZATION_KEY);
  } catch {
    return null;
  }
}
