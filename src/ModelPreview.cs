using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // Triangle soup exported by rz_bake.py "mesh" mode (Blender Z-up, world space).
    public class ModelMesh
    {
        public List<string> Materials = new List<string>();
        public int TriCount;
        public int[] TriMat, TriTile;
        public float[] Data; // per corner: x y z nx ny nz u v

        public static ModelMesh Load(string path)
        {
            using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RZM1")
                    throw new Exception("Not a preview mesh file.");
                ModelMesh m = new ModelMesh();
                int nm = r.ReadInt32();
                for (int i = 0; i < nm; i++)
                    m.Materials.Add(Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())));
                m.TriCount = r.ReadInt32();
                m.TriMat = new int[m.TriCount];
                m.TriTile = new int[m.TriCount];
                m.Data = new float[m.TriCount * 24];
                for (int t = 0; t < m.TriCount; t++)
                {
                    m.TriMat[t] = r.ReadInt32();
                    m.TriTile[t] = r.ReadInt32();
                    for (int k = 0; k < 24; k++)
                        m.Data[t * 24 + k] = r.ReadSingle();
                }
                return m;
            }
        }
    }

    // A texture ready for upload: BGRA rows bottom-up (OpenGL order).
    public class GLImage
    {
        public int W, H;
        public byte[] Data;

        public static GLImage FromBitmap(Bitmap bmp, int maxSize)
        {
            double sc = Math.Min(1.0, (double)maxSize / Math.Max(bmp.Width, bmp.Height));
            Bitmap b = sc < 1.0 ? Packer.EnsureSize(bmp, Math.Max(1, (int)(bmp.Width * sc)), Math.Max(1, (int)(bmp.Height * sc))) : bmp;
            GLImage img = new GLImage();
            img.W = b.Width;
            img.H = b.Height;
            img.Data = new byte[img.W * img.H * 4];
            BitmapData bd = b.LockBits(new Rectangle(0, 0, img.W, img.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < img.H; y++)
                Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), img.Data, (img.H - 1 - y) * img.W * 4, img.W * 4);
            b.UnlockBits(bd);
            if (b != bmp)
                b.Dispose();
            return img;
        }
    }

    // OpenGL PBR preview: GGX specular from roughness/metalness, tangent-space normal map, AO, a procedural sky
    // for reflections and ambient, ACES tone mapping. Left-drag = orbit, right/middle-drag = pan, wheel = zoom,
    // double-click = reset. Talks to opengl32.dll directly - no extra libraries.
    public class ModelPreview : Control
    {
        public const int ModePbr = 0, ModeAlbedo = 1, ModeRough = 2, ModeMetal = 3, ModeAo = 4, ModeNormal = 5, ModeClay = 6;

        // One texture set on the model: the triangles of one material inside one UDIM tile, with its own maps.
        private class Group
        {
            public string Key;
            public uint Vbo, TexAlb, TexNrm, TexOrm;
            public int Count;
            public bool HasMaps, MapsPending;
            public float FlipG = -1f;
            public GLImage PAlb, PNrm, POrm;
            public float[] PendingVerts;   // kept (with the maps) so a recreated GL context can re-upload
            public bool VertsDirty;
        }

        private IntPtr _dc, _rc;
        private bool _glOk;
        private uint _prog;
        private int _locMvp, _locCam, _locLight, _locTextured, _locMode, _locFlip, _locAlb, _locNrm, _locOrm;
        private int _mode;
        private Label _hint;
        private Dictionary<string, Group> _groups = new Dictionary<string, Group>();
        // 2x supersampling: render to an offscreen buffer twice the size, then filter it down
        private uint _fbo, _fboColor, _fboDepth;
        private int _fboW, _fboH;
        private bool _ssaa = true;

        private float[] _target = new float[3];
        private double _radius = 1, _yaw = 35, _pitch = 15, _dist = 3;
        private Point _last;
        private bool _orbit, _pan;

        public ModelPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            _hint = new Label();
            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.MiddleCenter;
            _hint.Dock = DockStyle.Fill;
            _hint.Text = "No model loaded";
            _hint.MouseDown += delegate(object s, MouseEventArgs e) { OnMouseDown(e); };
            Controls.Add(_hint);
            ApplyTheme();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20;        // CS_OWNDC - OpenGL wants its own device context
                cp.Style |= 0x02000000;       // WS_CLIPCHILDREN
                return cp;
            }
        }

        // Raised when a new GL context lost the uploaded textures (they aren't kept in memory - 4K sets are big).
        public event EventHandler MapsLost;

        public static string KeyFor(string material, int tile)
        {
            return material + "|" + tile;
        }

        public bool HasModel { get { return _groups.Count > 0; } }

        public void ApplyTheme()
        {
            BackColor = Theme.ThumbBg;
            _hint.BackColor = Theme.ThumbBg;
            _hint.ForeColor = Theme.SubText;
            Invalidate();
        }

        public void SetHint(string text)
        {
            _hint.Text = text ?? "";
            _hint.Visible = !string.IsNullOrEmpty(text);
        }

        public void SetMode(int mode)
        {
            _mode = mode;
            Invalidate();
        }

        // Maps for one texture set (null images = that map's neutral default). normalIsOpenGL: green up.
        public void SetGroupMaps(string key, GLImage albedo, GLImage normal, GLImage orm, bool normalIsOpenGL)
        {
            Group g;
            if (!_groups.TryGetValue(key, out g))
                return;
            g.PAlb = albedo; g.PNrm = normal; g.POrm = orm;
            g.FlipG = normalIsOpenGL ? 1f : -1f;
            g.MapsPending = true;
            Invalidate();
        }

        public void ClearGroupMaps(string key)
        {
            Group g;
            if (_groups.TryGetValue(key, out g))
            {
                g.HasMaps = false;
                g.MapsPending = false;
                g.PAlb = g.PNrm = g.POrm = null;
                Invalidate();
            }
        }

        // Splits the model into one group per (material, UDIM tile), each shown grey until it gets maps.
        public void SetMesh(ModelMesh m)
        {
            _groups.Clear(); // GL objects are reused/leaked-per-model only; fine for a preview
            if (m == null)
            {
                SetHint("No model loaded");
                Invalidate();
                return;
            }
            Dictionary<string, List<float>> verts = new Dictionary<string, List<float>>();
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            float[] d = m.Data;
            float[] P = new float[9], U = new float[6];
            for (int t = 0; t < m.TriCount; t++)
            {
                int tile = m.TriTile[t];
                string key = KeyFor(m.TriMat[t] < m.Materials.Count ? m.Materials[m.TriMat[t]] : "", tile);
                List<float> dst;
                if (!verts.TryGetValue(key, out dst))
                {
                    dst = new List<float>();
                    verts[key] = dst;
                }
                int tu = (tile - 1001) % 10, tv = (tile - 1001) / 10;
                for (int c = 0; c < 3; c++)
                {
                    int o = t * 24 + c * 8;
                    // Blender Z-up -> Y-up
                    P[c * 3] = d[o]; P[c * 3 + 1] = d[o + 2]; P[c * 3 + 2] = -d[o + 1];
                    U[c * 2] = d[o + 6] - tu; U[c * 2 + 1] = d[o + 7] - tv;
                }
                // per-triangle tangent from UVs (the shader re-orthogonalises it against the smooth normal)
                float e1x = P[3] - P[0], e1y = P[4] - P[1], e1z = P[5] - P[2];
                float e2x = P[6] - P[0], e2y = P[7] - P[1], e2z = P[8] - P[2];
                float du1 = U[2] - U[0], dv1 = U[3] - U[1], du2 = U[4] - U[0], dv2 = U[5] - U[1];
                float det = du1 * dv2 - du2 * dv1;
                float r = Math.Abs(det) < 1e-12f ? 0f : 1f / det;
                float tx = (e1x * dv2 - e2x * dv1) * r, ty = (e1y * dv2 - e2y * dv1) * r, tz = (e1z * dv2 - e2z * dv1) * r;
                float bx = (e2x * du1 - e1x * du2) * r, by = (e2y * du1 - e1y * du2) * r, bz = (e2z * du1 - e1z * du2) * r;
                float fnx = e1y * e2z - e1z * e2y, fny = e1z * e2x - e1x * e2z, fnz = e1x * e2y - e1y * e2x;
                float cx = fny * tz - fnz * ty, cy = fnz * tx - fnx * tz, cz = fnx * ty - fny * tx;
                float w = (cx * bx + cy * by + cz * bz) < 0f ? -1f : 1f;
                if (r == 0f) { tx = 1; ty = 0; tz = 0; w = 1; }
                for (int c = 0; c < 3; c++)
                {
                    int o = t * 24 + c * 8;
                    float x = P[c * 3], y = P[c * 3 + 1], z = P[c * 3 + 2];
                    dst.Add(x); dst.Add(y); dst.Add(z);
                    dst.Add(d[o + 3]); dst.Add(d[o + 5]); dst.Add(-d[o + 4]);
                    dst.Add(U[c * 2]); dst.Add(U[c * 2 + 1]);
                    dst.Add(tx); dst.Add(ty); dst.Add(tz); dst.Add(w);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
            }
            foreach (KeyValuePair<string, List<float>> kv in verts)
            {
                Group g = new Group();
                g.Key = kv.Key;
                g.PendingVerts = kv.Value.ToArray();
                g.VertsDirty = true;
                g.Count = g.PendingVerts.Length / 12;
                _groups[kv.Key] = g;
            }
            _target = new float[] { (minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2 };
            _radius = Math.Max(1e-3, Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ)) / 2);
            _yaw = 35; _pitch = 15; _dist = _radius * 2.6;
            SetHint(null);
            Invalidate();
        }

        // ---- OpenGL setup ---------------------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { InitGL(); }
            catch (Exception ex) { _glOk = false; SetHint("3D preview unavailable: " + ex.Message); }
            // a new context (e.g. after moving to the full-screen window) starts empty - upload everything again
            bool lost = false;
            foreach (Group g in _groups.Values)
            {
                g.Vbo = g.TexAlb = g.TexNrm = g.TexOrm = 0;
                g.VertsDirty = g.PendingVerts != null;
                if (g.HasMaps && !g.MapsPending) { g.HasMaps = false; lost = true; }
            }
            if (lost && MapsLost != null)
                BeginInvoke((Action)delegate { MapsLost(this, EventArgs.Empty); });
            _fbo = _fboColor = _fboDepth = 0;
            _fboW = _fboH = 0;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_rc != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(_rc);
                _rc = IntPtr.Zero;
            }
            if (_dc != IntPtr.Zero)
            {
                ReleaseDC(Handle, _dc);
                _dc = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        private void InitGL()
        {
            _dc = GetDC(Handle);
            PIXELFORMATDESCRIPTOR pfd = new PIXELFORMATDESCRIPTOR();
            pfd.nSize = (short)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR));
            pfd.nVersion = 1;
            pfd.dwFlags = 0x4 | 0x20 | 0x1; // draw to window, support OpenGL, double buffer
            pfd.cColorBits = 32;
            pfd.cDepthBits = 24;
            pfd.cStencilBits = 8;
            int fmt = ChoosePixelFormat(_dc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(_dc, fmt, ref pfd))
                throw new Exception("no OpenGL pixel format");
            _rc = wglCreateContext(_dc);
            if (_rc == IntPtr.Zero || !wglMakeCurrent(_dc, _rc))
                throw new Exception("could not create an OpenGL context");
            GL.Load();
            if (GL.CreateShader == null || GL.GenBuffers == null)
                throw new Exception("OpenGL 2.0 is required");
            _prog = BuildProgram(VertexSrc, FragmentSrc);
            _locMvp = GL.GetUniformLocation(_prog, "uMvp");
            _locCam = GL.GetUniformLocation(_prog, "uCam");
            _locLight = GL.GetUniformLocation(_prog, "uLightDir");
            _locTextured = GL.GetUniformLocation(_prog, "uTextured");
            _locMode = GL.GetUniformLocation(_prog, "uMode");
            _locFlip = GL.GetUniformLocation(_prog, "uFlipG");
            _locAlb = GL.GetUniformLocation(_prog, "uAlb");
            _locNrm = GL.GetUniformLocation(_prog, "uNrm");
            _locOrm = GL.GetUniformLocation(_prog, "uOrm");
            _ssaa = GL.GenFramebuffers != null && GL.BlitFramebuffer != null && GL.GenRenderbuffers != null;
            _glOk = true;
        }

        private static uint BuildProgram(string vs, string fs)
        {
            uint v = Compile(0x8B31, vs), f = Compile(0x8B30, fs);
            uint p = GL.CreateProgram();
            GL.AttachShader(p, v);
            GL.AttachShader(p, f);
            GL.BindAttribLocation(p, 0, "aPos");
            GL.BindAttribLocation(p, 1, "aNrm");
            GL.BindAttribLocation(p, 2, "aUv");
            GL.BindAttribLocation(p, 3, "aTan");
            GL.LinkProgram(p);
            int ok;
            GL.GetProgramiv(p, 0x8B82, out ok);
            if (ok == 0)
            {
                StringBuilder sb = new StringBuilder(4096);
                int len;
                GL.GetProgramInfoLog(p, sb.Capacity, out len, sb);
                throw new Exception("shader link failed: " + sb);
            }
            return p;
        }

        private static uint Compile(uint type, string src)
        {
            uint s = GL.CreateShader(type);
            GL.ShaderSource(s, 1, new string[] { src }, null);
            GL.CompileShader(s);
            int ok;
            GL.GetShaderiv(s, 0x8B81, out ok);
            if (ok == 0)
            {
                StringBuilder sb = new StringBuilder(4096);
                int len;
                GL.GetShaderInfoLog(s, sb.Capacity, out len, sb);
                throw new Exception("shader compile failed: " + sb);
            }
            return s;
        }

        // ---- uploads (always on the UI thread, inside the context) ---------------------------

        private void UploadPending()
        {
            foreach (Group g in _groups.Values)
            {
                if (g.VertsDirty && g.PendingVerts != null)
                {
                    if (g.Vbo == 0)
                    {
                        uint[] b = new uint[1];
                        GL.GenBuffers(1, b);
                        g.Vbo = b[0];
                    }
                    GL.BindBuffer(0x8892, g.Vbo);
                    GL.BufferData(0x8892, new IntPtr(g.PendingVerts.Length * 4), g.PendingVerts, 0x88E4);
                    g.VertsDirty = false;
                }
                if (g.MapsPending)
                {
                    if (g.TexAlb == 0)
                    {
                        uint[] t = new uint[3];
                        glGenTextures(3, t);
                        g.TexAlb = t[0]; g.TexNrm = t[1]; g.TexOrm = t[2];
                    }
                    Upload(g.TexAlb, g.PAlb, 128, 128, 128);
                    Upload(g.TexNrm, g.PNrm, 255, 128, 128);   // BGRA of flat (128,128,255)
                    Upload(g.TexOrm, g.POrm, 0, 128, 255);     // BGRA: AO 1, rough 0.5, metal 0
                    g.PAlb = g.PNrm = g.POrm = null;           // free the CPU copy - a 4K set is ~200 MB
                    g.MapsPending = false;
                    g.HasMaps = true;
                }
            }
        }

        private void Upload(uint id, GLImage img, byte b, byte g, byte r)
        {
            if (img == null)
            {
                img = new GLImage();
                img.W = img.H = 1;
                img.Data = new byte[] { b, g, r, 255 };
            }
            glBindTexture(0x0DE1, id);
            glPixelStorei(0x0CF5, 1);
            glTexImage2D(0x0DE1, 0, 0x8058, img.W, img.H, 0, 0x80E1, 0x1401, img.Data);
            glTexParameteri(0x0DE1, 0x2802, 0x2901);
            glTexParameteri(0x0DE1, 0x2803, 0x2901);
            glTexParameteri(0x0DE1, 0x2800, 0x2601);
            if (GL.GenerateMipmap != null)
            {
                GL.GenerateMipmap(0x0DE1);
                glTexParameteri(0x0DE1, 0x2801, 0x2703);
                glTexParameterf(0x0DE1, 0x84FE, 8f); // anisotropic, ignored if unsupported
            }
            else
                glTexParameteri(0x0DE1, 0x2801, 0x2601);
        }

        // Offscreen target at 2x the control size; false when framebuffers aren't available.
        private bool EnsureFbo(int w, int h)
        {
            if (!_ssaa)
                return false;
            if (_fbo != 0 && _fboW == w && _fboH == h)
                return true;
            uint[] a = new uint[1];
            if (_fbo == 0) { GL.GenFramebuffers(1, a); _fbo = a[0]; }
            if (_fboColor == 0) { GL.GenRenderbuffers(1, a); _fboColor = a[0]; }
            if (_fboDepth == 0) { GL.GenRenderbuffers(1, a); _fboDepth = a[0]; }
            GL.BindRenderbuffer(0x8D41, _fboColor);
            GL.RenderbufferStorage(0x8D41, 0x8058, w, h);            // RGBA8
            GL.BindRenderbuffer(0x8D41, _fboDepth);
            GL.RenderbufferStorage(0x8D41, 0x81A6, w, h);            // DEPTH_COMPONENT24
            GL.BindFramebuffer(0x8D40, _fbo);
            GL.FramebufferRenderbuffer(0x8D40, 0x8CE0, 0x8D41, _fboColor);
            GL.FramebufferRenderbuffer(0x8D40, 0x8D00, 0x8D41, _fboDepth);
            bool ok = GL.CheckFramebufferStatus(0x8D40) == 0x8CD5;
            GL.BindFramebuffer(0x8D40, 0);
            if (!ok)
            {
                _ssaa = false;
                return false;
            }
            _fboW = w; _fboH = h;
            return true;
        }

        // ---- drawing ------------------------------------------------------------------------

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_glOk)
            {
                e.Graphics.Clear(BackColor);
                return;
            }
            wglMakeCurrent(_dc, _rc);
            try { UploadPending(); }
            catch (Exception ex) { SetHint("3D preview error: " + ex.Message); }

            int cw = Math.Max(1, ClientSize.Width), ch = Math.Max(1, ClientSize.Height);
            bool ss = EnsureFbo(cw * 2, ch * 2);
            if (ss)
                GL.BindFramebuffer(0x8D40, _fbo);
            DrawScene(ss ? cw * 2 : cw, ss ? ch * 2 : ch);
            if (ss)
            {
                // 2x2 -> 1 with linear filtering = 4 samples per pixel
                GL.BindFramebuffer(0x8CA8, _fbo);   // READ
                GL.BindFramebuffer(0x8CA9, 0);      // DRAW
                GL.BlitFramebuffer(0, 0, cw * 2, ch * 2, 0, 0, cw, ch, 0x4000, 0x2601);
                GL.BindFramebuffer(0x8D40, 0);
            }
            SwapBuffers(_dc);
        }

        // Renders the model into the bound framebuffer at w x h.
        private void DrawScene(int w, int h)
        {
            glViewport(0, 0, w, h);
            Color bg = Theme.ThumbBg;
            glClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
            glClear(0x4000 | 0x100);
            if (_groups.Count > 0)
            {
                glEnable(0x0B71); // depth test
                GL.UseProgram(_prog);

                double yr = _yaw * Math.PI / 180, pr = _pitch * Math.PI / 180;
                float dx = (float)(Math.Cos(pr) * Math.Sin(yr)), dy = (float)Math.Sin(pr), dz = (float)(Math.Cos(pr) * Math.Cos(yr));
                float[] eye = { _target[0] + dx * (float)_dist, _target[1] + dy * (float)_dist, _target[2] + dz * (float)_dist };
                float aspect = (float)w / h;
                float near = (float)Math.Max(_dist * 0.01, 1e-3), far = (float)(_dist + _radius * 10);
                float[] mvp = Mul(Perspective(40f, aspect, near, far), LookAt(eye, _target));
                GL.UniformMatrix4fv(_locMvp, 1, 0, mvp);
                GL.Uniform3f(_locCam, eye[0], eye[1], eye[2]);
                // key light over the viewer's left shoulder, so whatever you look at is lit and shows its highlights
                float rx = dz, rz = -dx;
                GL.Uniform3f(_locLight, dx * 0.55f - rx * 0.45f, 0.75f, dz * 0.55f - rz * 0.45f);
                GL.Uniform1i(_locMode, _mode);
                GL.Uniform1i(_locAlb, 0);
                GL.Uniform1i(_locNrm, 1);
                GL.Uniform1i(_locOrm, 2);
                foreach (Group g in _groups.Values)
                {
                    GL.Uniform1i(_locTextured, g.HasMaps ? 1 : 0);
                    GL.Uniform1f(_locFlip, g.FlipG);
                    if (g.HasMaps)
                    {
                        GL.ActiveTexture(0x84C0); glBindTexture(0x0DE1, g.TexAlb);
                        GL.ActiveTexture(0x84C1); glBindTexture(0x0DE1, g.TexNrm);
                        GL.ActiveTexture(0x84C2); glBindTexture(0x0DE1, g.TexOrm);
                        GL.ActiveTexture(0x84C0);
                    }
                    DrawBuffer(g.Vbo, g.Count);
                }
                GL.UseProgram(0);
            }
        }

        // Renders the current view offscreen at w x h (supersampled) and returns it - for Save image.
        // Null when framebuffers aren't available.
        public Bitmap RenderImage(int w, int h)
        {
            if (!_glOk || !_ssaa || w < 1 || h < 1)
                return null;
            wglMakeCurrent(_dc, _rc);
            UploadPending();
            uint[] fb = new uint[2], rb = new uint[3];
            GL.GenFramebuffers(2, fb);
            GL.GenRenderbuffers(3, rb);
            try
            {
                // big = 2x with depth, small = the output size
                GL.BindRenderbuffer(0x8D41, rb[0]); GL.RenderbufferStorage(0x8D41, 0x8058, w * 2, h * 2);
                GL.BindRenderbuffer(0x8D41, rb[1]); GL.RenderbufferStorage(0x8D41, 0x81A6, w * 2, h * 2);
                GL.BindRenderbuffer(0x8D41, rb[2]); GL.RenderbufferStorage(0x8D41, 0x8058, w, h);
                GL.BindFramebuffer(0x8D40, fb[0]);
                GL.FramebufferRenderbuffer(0x8D40, 0x8CE0, 0x8D41, rb[0]);
                GL.FramebufferRenderbuffer(0x8D40, 0x8D00, 0x8D41, rb[1]);
                if (GL.CheckFramebufferStatus(0x8D40) != 0x8CD5) return null;
                DrawScene(w * 2, h * 2);
                GL.BindFramebuffer(0x8D40, fb[1]);
                GL.FramebufferRenderbuffer(0x8D40, 0x8CE0, 0x8D41, rb[2]);
                GL.BindFramebuffer(0x8CA8, fb[0]);
                GL.BindFramebuffer(0x8CA9, fb[1]);
                GL.BlitFramebuffer(0, 0, w * 2, h * 2, 0, 0, w, h, 0x4000, 0x2601);
                GL.BindFramebuffer(0x8CA8, fb[1]);
                byte[] px = new byte[w * h * 4];
                glPixelStorei(0x0D05, 1); // PACK_ALIGNMENT
                glReadPixels(0, 0, w, h, 0x80E1, 0x1401, px);
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w * 4; // GL rows are bottom-up
                    for (int x = 0; x < w; x++) px[src + x * 4 + 3] = 255;
                    Marshal.Copy(px, src, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4);
                }
                bmp.UnlockBits(bd);
                return bmp;
            }
            finally
            {
                GL.BindFramebuffer(0x8D40, 0);
                if (GL.DeleteFramebuffers != null) GL.DeleteFramebuffers(2, fb);
                if (GL.DeleteRenderbuffers != null) GL.DeleteRenderbuffers(3, rb);
                Invalidate();
            }
        }

        private void DrawBuffer(uint vbo, int count)
        {
            if (count == 0 || vbo == 0)
                return;
            GL.BindBuffer(0x8892, vbo);
            for (uint i = 0; i < 4; i++)
                GL.EnableVertexAttribArray(i);
            GL.VertexAttribPointer(0, 3, 0x1406, 0, 48, new IntPtr(0));
            GL.VertexAttribPointer(1, 3, 0x1406, 0, 48, new IntPtr(12));
            GL.VertexAttribPointer(2, 2, 0x1406, 0, 48, new IntPtr(24));
            GL.VertexAttribPointer(3, 4, 0x1406, 0, 48, new IntPtr(32));
            glDrawArrays(4, 0, count);
            for (uint i = 0; i < 4; i++)
                GL.DisableVertexAttribArray(i);
        }

        private static float[] Perspective(float fovDeg, float aspect, float n, float f)
        {
            float t = 1f / (float)Math.Tan(fovDeg * Math.PI / 360.0);
            float[] m = new float[16];
            m[0] = t / aspect; m[5] = t; m[10] = (f + n) / (n - f); m[11] = -1; m[14] = 2 * f * n / (n - f);
            return m;
        }

        private static float[] LookAt(float[] eye, float[] at)
        {
            float fx = at[0] - eye[0], fy = at[1] - eye[1], fz = at[2] - eye[2];
            float fl = (float)Math.Sqrt(fx * fx + fy * fy + fz * fz); fx /= fl; fy /= fl; fz /= fl;
            float sx = fy * 0 - fz * 1, sy = fz * 0 - fx * 0, sz = fx * 1 - fy * 0; // f x up(0,1,0)
            float sl = (float)Math.Sqrt(sx * sx + sy * sy + sz * sz); if (sl < 1e-6f) { sx = 1; sl = 1; } sx /= sl; sy /= sl; sz /= sl;
            float ux = sy * fz - sz * fy, uy = sz * fx - sx * fz, uz = sx * fy - sy * fx;
            float[] m = new float[16];
            m[0] = sx; m[4] = sy; m[8] = sz;
            m[1] = ux; m[5] = uy; m[9] = uz;
            m[2] = -fx; m[6] = -fy; m[10] = -fz;
            m[12] = -(sx * eye[0] + sy * eye[1] + sz * eye[2]);
            m[13] = -(ux * eye[0] + uy * eye[1] + uz * eye[2]);
            m[14] = fx * eye[0] + fy * eye[1] + fz * eye[2];
            m[15] = 1;
            return m;
        }

        // column-major a * b
        private static float[] Mul(float[] a, float[] b)
        {
            float[] r = new float[16];
            for (int c = 0; c < 4; c++)
                for (int rr = 0; rr < 4; rr++)
                {
                    float s = 0;
                    for (int k = 0; k < 4; k++)
                        s += a[k * 4 + rr] * b[c * 4 + k];
                    r[c * 4 + rr] = s;
                }
            return r;
        }

        // ---- mouse ----------------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _last = e.Location;
            if (e.Button == MouseButtons.Left) _orbit = true;
            else _pan = true;
            Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _orbit = _pan = false;
            Capture = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int dx = e.X - _last.X, dy = e.Y - _last.Y;
            _last = e.Location;
            if (_orbit)
            {
                _yaw -= dx * 0.5;
                _pitch = Math.Max(-89, Math.Min(89, _pitch + dy * 0.5));
                Invalidate();
            }
            else if (_pan)
            {
                double yr = _yaw * Math.PI / 180, pr = _pitch * Math.PI / 180;
                double fx = -Math.Cos(pr) * Math.Sin(yr), fy = -Math.Sin(pr), fz = -Math.Cos(pr) * Math.Cos(yr);
                double rx = -fz, rz = fx; // right = f x up, normalized below
                double rl = Math.Sqrt(rx * rx + rz * rz); rx /= rl; rz /= rl;
                double ux = -rz * fy, uy = rz * fx - rx * fz, uz = rx * fy;
                double k = _dist * 0.0018;
                _target[0] += (float)((-rx * dx + ux * dy) * k);
                _target[1] += (float)((uy * dy) * k);
                _target[2] += (float)((-rz * dx + uz * dy) * k);
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _dist *= e.Delta > 0 ? 0.87 : 1.15;
            _dist = Math.Max(_radius * 0.15, Math.Min(_radius * 20, _dist));
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            _yaw = 35; _pitch = 15; _dist = _radius * 2.6;
            Invalidate();
        }

        // ---- shaders --------------------------------------------------------------------------

        private const string VertexSrc = @"#version 120
attribute vec3 aPos; attribute vec3 aNrm; attribute vec2 aUv; attribute vec4 aTan;
uniform mat4 uMvp;
varying vec3 vPos; varying vec3 vNrm; varying vec2 vUv; varying vec4 vTan;
void main() { vPos = aPos; vNrm = aNrm; vUv = aUv; vTan = aTan; gl_Position = uMvp * vec4(aPos, 1.0); }
";

        private const string FragmentSrc = @"#version 120
varying vec3 vPos; varying vec3 vNrm; varying vec2 vUv; varying vec4 vTan;
uniform sampler2D uAlb; uniform sampler2D uNrm; uniform sampler2D uOrm;
uniform int uTextured; uniform int uMode; uniform float uFlipG;
uniform vec3 uCam; uniform vec3 uLightDir;
const float PI = 3.14159265;

// procedural outdoor sky: blue zenith, bright horizon, dark ground; blurred towards its average by roughness
vec3 sky(vec3 d, float rough) {
    float t = d.y;
    vec3 zen = vec3(0.30, 0.45, 0.80), hor = vec3(0.95, 0.95, 0.93), gnd = vec3(0.16, 0.14, 0.12);
    vec3 s = t > 0.0 ? mix(hor, zen, pow(t, 0.55)) : mix(hor * 0.55, gnd, pow(-t, 0.35));
    vec3 blur = mix(vec3(0.20, 0.19, 0.18), vec3(0.62, 0.68, 0.78), clamp(t * 0.5 + 0.5, 0.0, 1.0));
    return mix(s, blur, clamp(rough * 1.2, 0.0, 1.0));
}

void main() {
    vec3 N = normalize(vNrm);
    vec3 V = normalize(uCam - vPos);
    if (dot(N, V) < 0.0) N = -N; // back faces of single-sided parts
    vec3 alb = vec3(0.55); float ao = 1.0; float rough = 0.55; float metal = 0.0;
    vec3 nt = vec3(0.0, 0.0, 1.0);
    if (uTextured == 1) {
        alb = pow(texture2D(uAlb, vUv).rgb, vec3(2.2));
        vec3 orm = texture2D(uOrm, vUv).rgb;
        ao = orm.r; rough = orm.g; metal = orm.b;
        nt = texture2D(uNrm, vUv).rgb * 2.0 - 1.0;
        nt.y *= uFlipG;
        vec3 T = vTan.xyz - N * dot(N, vTan.xyz);
        if (dot(T, T) > 1e-8) {
            T = normalize(T);
            vec3 B = cross(N, T) * vTan.w;
            N = normalize(T * nt.x + B * nt.y + N * max(nt.z, 0.05));
        }
    }
    if (uMode == 1) { gl_FragColor = vec4(pow(alb, vec3(1.0 / 2.2)), 1.0); return; }
    if (uMode == 2) { gl_FragColor = vec4(vec3(rough), 1.0); return; }
    if (uMode == 3) { gl_FragColor = vec4(vec3(metal), 1.0); return; }
    if (uMode == 4) { gl_FragColor = vec4(vec3(ao), 1.0); return; }
    if (uMode == 5) { gl_FragColor = vec4(nt * 0.5 + 0.5, 1.0); return; }
    if (uMode == 6) { alb = vec3(0.32); rough = 0.55; metal = 0.0; }

    rough = clamp(rough, 0.045, 1.0);
    vec3 L = normalize(uLightDir);
    vec3 H = normalize(V + L);
    float NdL = max(dot(N, L), 0.0), NdV = max(dot(N, V), 1e-3), NdH = max(dot(N, H), 0.0), VdH = max(dot(V, H), 0.0);
    float a = rough * rough, a2 = a * a;
    float dd = NdH * NdH * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * dd * dd);
    float k = (rough + 1.0) * (rough + 1.0) / 8.0;
    float G = (NdV / (NdV * (1.0 - k) + k)) * (NdL / (NdL * (1.0 - k) + k));
    vec3 F0 = mix(vec3(0.04), alb, metal);
    vec3 F = F0 + (1.0 - F0) * pow(1.0 - VdH, 5.0);
    vec3 spec = D * G * F / (4.0 * NdV * max(NdL, 1e-3) + 1e-4);
    vec3 kd = (1.0 - F) * (1.0 - metal);
    vec3 direct = (kd * alb / PI + spec) * vec3(3.2, 3.1, 2.9) * NdL;

    vec3 R = reflect(-V, N);
    vec3 Fa = F0 + (max(vec3(1.0 - rough), F0) - F0) * pow(1.0 - NdV, 5.0);
    vec3 diffuseAmb = (1.0 - Fa) * (1.0 - metal) * alb * sky(N, 1.0);
    vec3 specAmb = Fa * sky(R, rough) * (1.0 - 0.6 * rough * rough);
    vec3 col = direct + (diffuseAmb + specAmb) * ao;

    col = col * (2.51 * col + 0.03) / (col * (2.43 * col + 0.59) + 0.14); // ACES fit
    gl_FragColor = vec4(pow(clamp(col, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
}
";

        // ---- Win32 / OpenGL imports -------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct PIXELFORMATDESCRIPTOR
        {
            public short nSize, nVersion;
            public int dwFlags;
            public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
                        cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
                        cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public int dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);
        [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
        [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);
        [DllImport("opengl32.dll")] internal static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] private static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32.dll")] private static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] private static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] private static extern void glEnable(uint cap);
        [DllImport("opengl32.dll")] private static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport("opengl32.dll")] private static extern void glGenTextures(int n, uint[] textures);
        [DllImport("opengl32.dll")] private static extern void glBindTexture(uint target, uint texture);
        [DllImport("opengl32.dll")] private static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, byte[] data);
        [DllImport("opengl32.dll")] private static extern void glTexParameteri(uint target, uint pname, int param);
        [DllImport("opengl32.dll")] private static extern void glTexParameterf(uint target, uint pname, float param);
        [DllImport("opengl32.dll")] private static extern void glPixelStorei(uint pname, int param);
        [DllImport("opengl32.dll")] private static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, byte[] data);
    }

    // OpenGL 2.0+ entry points, fetched from the driver at runtime.
    internal static class GL
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint dCreateShader(uint type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dShaderSource(uint shader, int count, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] src, int[] lengths);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUint(uint a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dGetiv(uint obj, uint pname, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] public delegate void dGetLog(uint obj, int maxLen, out int len, StringBuilder log);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint dCreateProgram();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUintUint(uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] public delegate void dBindAttrib(uint prog, uint index, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] public delegate int dGetUniformLocation(uint prog, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUniform1i(int loc, int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUniform1f(int loc, float v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUniform3f(int loc, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dUniformMatrix4fv(int loc, int count, byte transpose, float[] v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dGenBuffers(int n, uint[] buffers);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dBufferData(uint target, IntPtr size, float[] data, uint usage);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dRenderbufferStorage(uint target, uint format, int w, int h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dFramebufferRenderbuffer(uint target, uint attachment, uint rbTarget, uint rb);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint dCheckFramebufferStatus(uint target);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void dBlitFramebuffer(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);

        public static dCreateShader CreateShader;
        public static dShaderSource ShaderSource;
        public static dUint CompileShader, LinkProgram, UseProgram, EnableVertexAttribArray, DisableVertexAttribArray, ActiveTexture, GenerateMipmap;
        public static dGetiv GetShaderiv, GetProgramiv;
        public static dGetLog GetShaderInfoLog, GetProgramInfoLog;
        public static dCreateProgram CreateProgram;
        public static dUintUint AttachShader, BindBuffer;
        public static dBindAttrib BindAttribLocation;
        public static dGetUniformLocation GetUniformLocation;
        public static dUniform1i Uniform1i;
        public static dUniform1f Uniform1f;
        public static dUniform3f Uniform3f;
        public static dUniformMatrix4fv UniformMatrix4fv;
        public static dGenBuffers GenBuffers, GenFramebuffers, GenRenderbuffers, DeleteFramebuffers, DeleteRenderbuffers;
        public static dUintUint BindFramebuffer, BindRenderbuffer;
        public static dRenderbufferStorage RenderbufferStorage;
        public static dFramebufferRenderbuffer FramebufferRenderbuffer;
        public static dCheckFramebufferStatus CheckFramebufferStatus;
        public static dBlitFramebuffer BlitFramebuffer;
        public static dBufferData BufferData;
        public static dVertexAttribPointer VertexAttribPointer;

        private static T F<T>(string name) where T : class
        {
            IntPtr p = ModelPreview.wglGetProcAddress(name);
            long v = p.ToInt64();
            if (v == 0 || v == 1 || v == 2 || v == 3 || v == -1)
                return null;
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public static void Load()
        {
            CreateShader = F<dCreateShader>("glCreateShader");
            ShaderSource = F<dShaderSource>("glShaderSource");
            CompileShader = F<dUint>("glCompileShader");
            GetShaderiv = F<dGetiv>("glGetShaderiv");
            GetShaderInfoLog = F<dGetLog>("glGetShaderInfoLog");
            CreateProgram = F<dCreateProgram>("glCreateProgram");
            AttachShader = F<dUintUint>("glAttachShader");
            BindAttribLocation = F<dBindAttrib>("glBindAttribLocation");
            LinkProgram = F<dUint>("glLinkProgram");
            GetProgramiv = F<dGetiv>("glGetProgramiv");
            GetProgramInfoLog = F<dGetLog>("glGetProgramInfoLog");
            UseProgram = F<dUint>("glUseProgram");
            GetUniformLocation = F<dGetUniformLocation>("glGetUniformLocation");
            Uniform1i = F<dUniform1i>("glUniform1i");
            Uniform1f = F<dUniform1f>("glUniform1f");
            Uniform3f = F<dUniform3f>("glUniform3f");
            UniformMatrix4fv = F<dUniformMatrix4fv>("glUniformMatrix4fv");
            GenBuffers = F<dGenBuffers>("glGenBuffers");
            BindBuffer = F<dUintUint>("glBindBuffer");
            BufferData = F<dBufferData>("glBufferData");
            EnableVertexAttribArray = F<dUint>("glEnableVertexAttribArray");
            DisableVertexAttribArray = F<dUint>("glDisableVertexAttribArray");
            VertexAttribPointer = F<dVertexAttribPointer>("glVertexAttribPointer");
            ActiveTexture = F<dUint>("glActiveTexture");
            GenerateMipmap = F<dUint>("glGenerateMipmap");
            GenFramebuffers = F<dGenBuffers>("glGenFramebuffers");
            GenRenderbuffers = F<dGenBuffers>("glGenRenderbuffers");
            DeleteFramebuffers = F<dGenBuffers>("glDeleteFramebuffers");
            DeleteRenderbuffers = F<dGenBuffers>("glDeleteRenderbuffers");
            BindFramebuffer = F<dUintUint>("glBindFramebuffer");
            BindRenderbuffer = F<dUintUint>("glBindRenderbuffer");
            RenderbufferStorage = F<dRenderbufferStorage>("glRenderbufferStorage");
            FramebufferRenderbuffer = F<dFramebufferRenderbuffer>("glFramebufferRenderbuffer");
            CheckFramebufferStatus = F<dCheckFramebufferStatus>("glCheckFramebufferStatus");
            BlitFramebuffer = F<dBlitFramebuffer>("glBlitFramebuffer");
        }
    }
}
