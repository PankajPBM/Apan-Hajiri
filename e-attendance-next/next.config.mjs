/** @type {import('next').NextConfig} */
const nextConfig = {
  typedRoutes: true,
  output: "export",
  trailingSlash: true,
  typescript: {
    ignoreBuildErrors: true,
  },
};

export default nextConfig;
