import { praxyMiddleware } from "@praxy/nextjs/middleware";
import { projectId } from "@/lib/config";

export default praxyMiddleware({
  projectId,
  protectedPaths: ["/dashboard"],
  signInUrl: "/",
});

export const config = {
  matcher: ["/dashboard/:path*"],
};
