// Copyright(C) David W. Jeske, 2013
// Released to the public domain. Use, modify and relicense at will.

using System;
using System.Collections.Generic;


using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace SimpleScene
{
	public enum WireframeMode {
		None,
		GLSL_SinglePass,
		GL_Lines,
	};

	public struct SSRenderStats {
		public int objectsDrawn;
		public int objectsCulled;
	}     

	public class SSRenderConfig {
		public SSRenderStats renderStats;

		public SSMainShaderProgram BaseShader;

		public bool drawGLSL = true;
		public bool useVBO = true;
        public bool drawingShadowMap = false;

		public bool renderBoundingSpheres;
		public bool renderCollisionShells;

		public bool frustumCulling;

		public WireframeMode drawWireframeMode;
		public Matrix4 invCameraViewMat;
		public Matrix4 projectionMatrix;

		public static void toggle(ref WireframeMode val) {
			int value = (int)val;
			value++;
			if (value > (int)WireframeMode.GL_Lines) {
				value = (int)WireframeMode.None;
			}
			val = (WireframeMode)value;
		}
	}

    public sealed class SSScene
    {
        private SSCamera m_activeCamera = null;
        private SSRenderConfig m_renderConfig = new SSRenderConfig();
        private List<SSObject> m_objects = new List<SSObject>();
        private List<SSLight> m_lights = new List<SSLight>();
        private List<SSShadowMap> m_shadowMaps = new List<SSShadowMap> ();

        public List <SSObject> Objects { get { return m_objects; } }

        public SSCamera ActiveCamera { 
            get { return m_activeCamera; }
            set { m_activeCamera = value; }
        }

        public SSRenderConfig RenderConfig { 
            get { return m_renderConfig; } 
            set { m_renderConfig = value; } 
        }

        public Matrix4 ProjectionMatrix {
            get { return m_renderConfig.projectionMatrix; }
            set { m_renderConfig.projectionMatrix = value; }
        }

        public Matrix4 InvCameraViewMatrix {
            get { return m_renderConfig.invCameraViewMat; }
            set { m_renderConfig.invCameraViewMat = value; }
        }

        #region SSScene Events
        public delegate void BeforeRenderObjectHandler(SSObject obj, SSRenderConfig renderConfig);
        public event BeforeRenderObjectHandler BeforeRenderObject;
        #endregion

        public void AddObject(SSObject obj) {
            m_objects.Add(obj);
        }

        public void RemoveObject(SSObject obj) {
            // todo threading
            m_objects.Remove(obj);
        }

        public void AddLight(SSLight light) {
            if (m_lights.Contains(light)) {
                return;
            }
            m_lights.Add(light);
            if (light.ShadowMap != null) {
                m_shadowMaps.Add(light.ShadowMap);
            }
        }

        public void RemoveLight(SSLight light) {
            if (!m_lights.Contains(light)) {
                throw new Exception ("Light not found.");
            }
            if (light.ShadowMap != null) {
                m_shadowMaps.Remove(light.ShadowMap);
            }
            m_lights.Remove(light);
        }

        public SSObject Intersect(ref SSRay worldSpaceRay) {
            SSObject nearestIntersection = null;
            float nearestDistance = float.MinValue;
            // distances get "smaller" as they move in camera direction for some reason (why?)
            foreach (var obj in m_objects) {
                float distanceAlongRay;
                if (obj.Intersect(ref worldSpaceRay, out distanceAlongRay)) {
                    // intersection must be in front of the camera ( < 0.0 )
                    if (distanceAlongRay < 0.0) {
                        Console.WriteLine("intersect: [{0}] @distance: {1}", obj.Name, distanceAlongRay);
                        // then we want the nearest one (numerically biggest
                        if (distanceAlongRay > nearestDistance) {
                            nearestDistance = distanceAlongRay;
                            nearestIntersection = obj;
                        }
                    }
                }
            }

            return nearestIntersection;
        }

        public void Update(float fElapsedMS) {
            // update all objects.. TODO: add elapsed time since last update..
            foreach (var obj in m_objects) {
                obj.Update(fElapsedMS);
            }
        }

        public void Render() {
			setupLights ();
            // Shadow Map Pass(es)
            foreach (var light in m_lights) {
                if (light.ShadowMap != null) {
                    light.ShadowMap.PrepareForRender(m_renderConfig);
                    renderPass(false);
                    light.ShadowMap.FinishRender(m_renderConfig);
                }
            }

            // update mvp and textures for shadowmaps in the main shader
            if (m_renderConfig.BaseShader != null) {
                m_renderConfig.BaseShader.Activate();
                m_renderConfig.BaseShader.ShadowMaps = m_shadowMaps;
            }
            // compute a world-space frustum matrix, so we can test against world-space object positions
            Matrix4 frustumMatrix = m_renderConfig.invCameraViewMat * m_renderConfig.projectionMatrix;
            renderPass(true, new Util3d.FrustumCuller(ref frustumMatrix));
        }

        private void setupLights() {
            // setup the projection matrix

            GL.Enable(EnableCap.Lighting);
            foreach (var light in m_lights) {
                light.SetupLight(ref m_renderConfig);
            }
        }

        private void renderPass(bool notifyBeforeRender, Util3d.FrustumCuller fc = null) {
            // reset stats
            m_renderConfig.renderStats = new SSRenderStats();

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref m_renderConfig.projectionMatrix);

            bool needObjectDelete = false;

            foreach (var obj in m_objects) {
                if (obj.renderState.toBeDeleted) { needObjectDelete = true; continue; }
                if (!obj.renderState.visible) continue; // skip invisible objects
                // frustum test... 
                #if true
                if (m_renderConfig.frustumCulling &&
                    fc != null &&
                    obj.boundingSphere != null &&
                    !fc.isSphereInsideFrustum(obj.Pos, obj.boundingSphere.radius * obj.Scale.LengthFast)) {
                    m_renderConfig.renderStats.objectsCulled++;
                    continue; // skip the object
                }
                #endif

                // finally, render object
                if (notifyBeforeRender && BeforeRenderObject != null) {
                    BeforeRenderObject(obj, m_renderConfig);
                }
                m_renderConfig.renderStats.objectsDrawn++;
                obj.Render(ref m_renderConfig);
            }

            if (needObjectDelete) {
                m_objects.RemoveAll(o => o.renderState.toBeDeleted);
            }
        }

        public SSScene() {
            // Register SS types for loading by SSAssetManager
            SSAssetManager.RegisterLoadDelegate<SSTexture>(
                (ctx, filename) => { return new SSTexture(ctx, filename); }
            );
            SSAssetManager.RegisterLoadDelegate<SSMesh_wfOBJ>(
                (ctx, filename) => { return new SSMesh_wfOBJ(ctx, filename); }
            );
            SSAssetManager.RegisterLoadDelegate<SSVertexShader>(
                (ctx, filename) => { return new SSVertexShader(ctx, filename); }
            );
            SSAssetManager.RegisterLoadDelegate<SSFragmentShader>(
                (ctx, filename) => { return new SSFragmentShader(ctx, filename); }
            );
            SSAssetManager.RegisterLoadDelegate<SSGeometryShader>(
                (ctx, filename) => { return new SSGeometryShader(ctx, filename); }
            );
        }
    }
}

